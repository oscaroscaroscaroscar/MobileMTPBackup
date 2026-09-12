using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MobileMTPBackup;

public sealed record WifiMediaItem(long Id,string Name,long Size,long ModifiedUnix,string Mime,string RelativePath);

public sealed class WifiCompanionClient(string host,int port,string pairingCode)
{
    private async Task<(TcpClient Client,NetworkStream Stream,byte[] SessionKey)> ConnectAuthenticatedAsync(string command,CancellationToken ct)
    {
        var client=new TcpClient();
        await client.ConnectAsync(host,port,ct);
        var stream=client.GetStream();
        string challenge=await ReadAsciiLineAsync(stream,ct);
        if(!challenge.StartsWith("NONCE ",StringComparison.Ordinal)) { client.Dispose(); throw new IOException("Companion saknar säker nonce-handshake."); }
        string nonce=challenge[6..].Trim();
        if(nonce.Length!=32||nonce.Any(c=>!Uri.IsHexDigit(c))) { client.Dispose(); throw new IOException("Ogiltig nonce från Companion."); }
        string payload=nonce+"\n"+command;
        using var hmac=new HMACSHA256(Encoding.UTF8.GetBytes(pairingCode));
        string signature=Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        byte[] request=Encoding.UTF8.GetBytes($"AUTH {signature} {command}\n");
        await stream.WriteAsync(request,ct);await stream.FlushAsync(ct);
        return (client,stream,DeriveSessionKey(nonce));
    }

    private byte[] DeriveSessionKey(string nonce)
        => SHA256.HashData(Encoding.UTF8.GetBytes($"MobileMTPBackup-v5.24\n{pairingCode}\n{nonce}"));

    public async Task<string> HelloAsync(CancellationToken ct)
    {
        var pair=await ConnectAuthenticatedAsync("HELLO",ct);using var client=pair.Client;using var stream=pair.Stream;
        string reply=await ReadAsciiLineAsync(stream,ct);
        if(reply.StartsWith("ERROR ",StringComparison.Ordinal))throw new IOException(reply);
        return reply;
    }

    public async Task<IReadOnlyList<WifiMediaItem>> ListAsync(CancellationToken ct)
    {
        var pair=await ConnectAuthenticatedAsync("LIST",ct);using var client=pair.Client;using var stream=pair.Stream;
        using var reader=new StreamReader(stream,Encoding.UTF8,false,4096,true);var result=new List<WifiMediaItem>();
        while(true){string? line=await reader.ReadLineAsync(ct);if(line is null||line=="END")break;if(line.StartsWith("ERROR ",StringComparison.Ordinal))throw new IOException("Companion LIST-fel: "+line);var p=line.Split('\t');if(p.Length<5)continue;if(long.TryParse(p[0],out var id)&&long.TryParse(p[2],out var size)&&long.TryParse(p[3],out var modified))result.Add(new(id,p[1],size,modified,p[4],p.Length>=6?p[5]:""));}
        return result;
    }

    public async Task<bool> CanSkipExistingAsync(WifiMediaItem item,string destination,bool verifyHash,CancellationToken ct)
    {
        if(!File.Exists(destination))return false;var info=new FileInfo(destination);if(item.Size>=0&&info.Length!=item.Size)return false;
        if(verifyHash){string remoteHash=await HashAsync(item.Id,ct);if(remoteHash.Length!=64||remoteHash.Any(c=>!Uri.IsHexDigit(c)))return false;await using var input=File.OpenRead(destination);string localHash=Convert.ToHexString(await SHA256.HashDataAsync(input,ct)).ToLowerInvariant();return string.Equals(remoteHash,localHash,StringComparison.OrdinalIgnoreCase);}
        if(item.ModifiedUnix>0){DateTime remote=DateTimeOffset.FromUnixTimeSeconds(item.ModifiedUnix).LocalDateTime;return Math.Abs((info.LastWriteTime-remote).TotalSeconds)<=2;}return true;
    }

    public async Task DownloadVerifiedAsync(WifiMediaItem item,string destination,CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);string partial=destination+".partial";
        try{
            try{if(File.Exists(partial))File.Delete(partial);}catch{}
            var pair=await ConnectAuthenticatedAsync($"GET {item.Id}",ct);
            using(var client=pair.Client)
            using(var stream=pair.Stream)
            {
                string header=await ReadAsciiLineAsync(stream,ct);
                if(header.StartsWith("ERROR ",StringComparison.Ordinal))throw new IOException("Companion GET-fel: "+header);
                var hp=header.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                if(hp.Length!=3||hp[0]!="EDATA"||!long.TryParse(hp[1],out long expected)||!int.TryParse(hp[2],out int chunkSize))throw new IOException("Ogiltigt krypterat GET-svar: "+header);
                if(expected<0||chunkSize<16||chunkSize>4*1024*1024)throw new IOException("Ogiltiga krypteringsparametrar från telefonen.");
                if(item.Size>0&&expected!=item.Size)throw new IOException("Filstorleken ändrades på telefonen.");
                await using var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,true);
                using var aes=new AesGcm(pair.SessionKey,16);
                long written=0;
                byte[] lenBytes=new byte[4];
                while(written<expected)
                {
                    await ReadExactlyAsync(stream,lenBytes,ct);
                    int plainLength=BinaryPrimitives.ReadInt32BigEndian(lenBytes);
                    if(plainLength<=0||plainLength>chunkSize||written+plainLength>expected)throw new IOException("Ogiltig krypterad blockstorlek.");
                    byte[] nonce=new byte[12];await ReadExactlyAsync(stream,nonce,ct);
                    byte[] encrypted=new byte[plainLength+16];await ReadExactlyAsync(stream,encrypted,ct);
                    byte[] plain=new byte[plainLength];
                    aes.Decrypt(nonce,encrypted.AsSpan(0,plainLength),encrypted.AsSpan(plainLength,16),plain);
                    await output.WriteAsync(plain,ct);written+=plainLength;
                }
                await output.FlushAsync(ct);
            }
            long actual=new FileInfo(partial).Length;if(item.Size>0&&actual!=item.Size)throw new IOException($"Nedladdad filstorlek stämmer inte: {actual} != {item.Size}.");string remoteHash=await HashAsync(item.Id,ct);if(remoteHash.Length!=64||remoteHash.Any(c=>!Uri.IsHexDigit(c)))throw new IOException("Ogiltigt SHA-256-svar från telefonen.");await using var input=File.OpenRead(partial);string localHash=Convert.ToHexString(await SHA256.HashDataAsync(input,ct)).ToLowerInvariant();if(!string.Equals(remoteHash,localHash,StringComparison.OrdinalIgnoreCase))throw new IOException("SHA-256 stämmer inte.");if(File.Exists(destination))throw new IOException("Målfilen finns redan men matchar inte källan: "+destination);File.Move(partial,destination);if(item.ModifiedUnix>0){var dt=DateTimeOffset.FromUnixTimeSeconds(item.ModifiedUnix).LocalDateTime;try{File.SetLastWriteTime(destination,dt);}catch{}}
        }catch{try{if(File.Exists(partial))File.Delete(partial);}catch{}throw;}
    }

    public async Task<string> HashAsync(long id,CancellationToken ct){var pair=await ConnectAuthenticatedAsync($"HASH {id}",ct);using var client=pair.Client;using var stream=pair.Stream;string reply=(await ReadAsciiLineAsync(stream,ct)).Trim();if(reply.StartsWith("ERROR ",StringComparison.Ordinal))throw new IOException("Companion HASH-fel: "+reply);return reply;}

    private static async Task ReadExactlyAsync(Stream stream,Memory<byte> buffer,CancellationToken ct)
    {
        int offset=0;while(offset<buffer.Length){int n=await stream.ReadAsync(buffer[offset..],ct);if(n<=0)throw new EndOfStreamException("Wi-Fi-överföringen avbröts mitt i ett krypterat block.");offset+=n;}
    }

    private static async Task<string> ReadAsciiLineAsync(Stream stream,CancellationToken ct){var bytes=new List<byte>(128);var one=new byte[1];while(true){int n=await stream.ReadAsync(one,ct);if(n==0)break;if(one[0]==10)break;if(one[0]!=13)bytes.Add(one[0]);if(bytes.Count>4096)throw new IOException("För långt protokollhuvud.");}return Encoding.UTF8.GetString(bytes.ToArray());}
}
