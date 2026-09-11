using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MobileMTPBackup;

public sealed record WifiMediaItem(long Id,string Name,long Size,long ModifiedUnix,string Mime);

public sealed class WifiCompanionClient(string host,int port,string pairingCode)
{
    private async Task<TcpClient> ConnectAsync(CancellationToken ct)
    {
        var client=new TcpClient();
        await client.ConnectAsync(host,port,ct);
        return client;
    }

    private byte[] Command(string command)=>Encoding.UTF8.GetBytes($"AUTH {pairingCode} {command}\n");

    public async Task<IReadOnlyList<WifiMediaItem>> ListAsync(CancellationToken ct)
    {
        using var client=await ConnectAsync(ct);using var stream=client.GetStream();
        await stream.WriteAsync(Command("LIST"),ct);await stream.FlushAsync(ct);
        using var reader=new StreamReader(stream,Encoding.UTF8,false,4096,true);
        var result=new List<WifiMediaItem>();
        while(true)
        {
            string? line=await reader.ReadLineAsync(ct);if(line is null||line=="END")break;
            if(line.StartsWith("ERROR ",StringComparison.Ordinal))throw new IOException("Companion LIST-fel: "+line);
            var p=line.Split('\t');if(p.Length<5)continue;
            if(long.TryParse(p[0],out var id)&&long.TryParse(p[2],out var size)&&long.TryParse(p[3],out var modified))result.Add(new(id,p[1],size,modified,p[4]));
        }
        return result;
    }

    public async Task<bool> CanSkipExistingAsync(WifiMediaItem item,string destination,bool verifyHash,CancellationToken ct)
    {
        if(!File.Exists(destination))return false;
        var info=new FileInfo(destination);
        if(item.Size>=0&&info.Length!=item.Size)return false;

        if(verifyHash)
        {
            string remoteHash=await HashAsync(item.Id,ct);
            if(remoteHash.Length!=64||remoteHash.Any(c=>!Uri.IsHexDigit(c)))return false;
            await using var input=File.OpenRead(destination);
            string localHash=Convert.ToHexString(await SHA256.HashDataAsync(input,ct)).ToLowerInvariant();
            return string.Equals(remoteHash,localHash,StringComparison.OrdinalIgnoreCase);
        }

        if(item.ModifiedUnix>0)
        {
            DateTime remote=DateTimeOffset.FromUnixTimeSeconds(item.ModifiedUnix).LocalDateTime;
            return Math.Abs((info.LastWriteTime-remote).TotalSeconds)<=2;
        }
        return true;
    }

    public async Task DownloadVerifiedAsync(WifiMediaItem item,string destination,CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string partial=destination+".partial";
        try
        {
            try{if(File.Exists(partial))File.Delete(partial);}catch{}
            using(var client=await ConnectAsync(ct))
            using(var stream=client.GetStream())
            {
                await stream.WriteAsync(Command($"GET {item.Id}"),ct);await stream.FlushAsync(ct);
                string header=await ReadAsciiLineAsync(stream,ct);
                if(header.StartsWith("ERROR ",StringComparison.Ordinal))throw new IOException("Companion GET-fel: "+header);
                if(!header.StartsWith("DATA ",StringComparison.Ordinal)||!long.TryParse(header[5..],out long expected))throw new IOException("Ogiltigt GET-svar: "+header);
                if(expected<0)throw new IOException("Ogiltig filstorlek från telefonen.");
                if(item.Size>0&&expected!=item.Size)throw new IOException("Filstorleken ändrades på telefonen.");
                await using var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,true);
                byte[] buffer=new byte[1024*1024];long remaining=expected;
                while(remaining>0)
                {
                    int n=await stream.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,remaining)),ct);
                    if(n<=0)throw new EndOfStreamException("Wi-Fi-överföringen avbröts.");
                    await output.WriteAsync(buffer.AsMemory(0,n),ct);remaining-=n;
                }
                await output.FlushAsync(ct);
            }
            long actual=new FileInfo(partial).Length;if(item.Size>0&&actual!=item.Size)throw new IOException($"Nedladdad filstorlek stämmer inte: {actual} != {item.Size}.");
            string remoteHash=await HashAsync(item.Id,ct);
            if(remoteHash.Length!=64||remoteHash.Any(c=>!Uri.IsHexDigit(c)))throw new IOException("Ogiltigt SHA-256-svar från telefonen.");
            await using var input=File.OpenRead(partial);
            string localHash=Convert.ToHexString(await SHA256.HashDataAsync(input,ct)).ToLowerInvariant();
            if(!string.Equals(remoteHash,localHash,StringComparison.OrdinalIgnoreCase))throw new IOException("SHA-256 stämmer inte.");
            if(File.Exists(destination))throw new IOException("Målfilen finns redan men matchar inte källan: "+destination);
            File.Move(partial,destination);
            if(item.ModifiedUnix>0){var dt=DateTimeOffset.FromUnixTimeSeconds(item.ModifiedUnix).LocalDateTime;try{File.SetLastWriteTime(destination,dt);}catch{}}
        }
        catch
        {
            try{if(File.Exists(partial))File.Delete(partial);}catch{}
            throw;
        }
    }

    public async Task<string> HashAsync(long id,CancellationToken ct)
    {
        using var client=await ConnectAsync(ct);using var stream=client.GetStream();
        await stream.WriteAsync(Command($"HASH {id}"),ct);await stream.FlushAsync(ct);
        string reply=(await ReadAsciiLineAsync(stream,ct)).Trim();
        if(reply.StartsWith("ERROR ",StringComparison.Ordinal))throw new IOException("Companion HASH-fel: "+reply);
        return reply;
    }

    private static async Task<string> ReadAsciiLineAsync(Stream stream,CancellationToken ct)
    {
        var bytes=new List<byte>(128);var one=new byte[1];
        while(true){int n=await stream.ReadAsync(one,ct);if(n==0)break;if(one[0]==10)break;if(one[0]!=13)bytes.Add(one[0]);if(bytes.Count>4096)throw new IOException("För långt protokollhuvud.");}
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
