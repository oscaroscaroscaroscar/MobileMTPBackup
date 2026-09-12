using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MobileMTPBackup;

const string ProtocolVersion = "MobileMTPBackup-v5.27";
const string PairingCode = "0011223344556677";
byte[] MediaBytes = Encoding.UTF8.GetBytes("Mobile MTP Backup protocol self-test payload v5.28\n");
long MediaId = 42;
long ModifiedUnix = 1_700_000_000;

using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;
using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var serverTask = RunServerAsync(listener, serverCts.Token);

var client = new WifiCompanionClient("127.0.0.1", port, PairingCode);
using var clientCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

string hello = await client.HelloAsync(clientCts.Token);
Assert(hello == "MOBILE_MTP_BACKUP_COMPANION/0.11", "HELLO svar fel");

var list = await client.ListAsync(clientCts.Token);
Assert(list.Count == 1, "LIST antal fel");
var item = list[0];
Assert(item.Id == MediaId, "LIST id fel");
Assert(item.Name == "selftest.txt", "LIST namn fel");
Assert(item.Size == MediaBytes.LongLength, "LIST storlek fel");
Assert(item.RelativePath == "DCIM/SelfTest/", "LIST relativ sokvag fel");

string tempRoot = Path.Combine(Path.GetTempPath(), "MobileMTPBackup-ProtocolSelfTest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
string target = Path.Combine(tempRoot, "selftest.txt");
try
{
    await client.DownloadVerifiedAsync(item, target, clientCts.Token);
    byte[] downloaded = await File.ReadAllBytesAsync(target, clientCts.Token);
    Assert(downloaded.SequenceEqual(MediaBytes), "GET innehall fel");
    string expectedHash = Convert.ToHexString(SHA256.HashData(MediaBytes)).ToLowerInvariant();
    string remoteHash = await client.HashAsync(MediaId, clientCts.Token);
    Assert(remoteHash == expectedHash, "HASH svar fel");
    bool skip = await client.CanSkipExistingAsync(item, target, true, clientCts.Token);
    Assert(skip, "Inkrementell hashkontroll borde hoppa over filen");
}
finally
{
    try { Directory.Delete(tempRoot, true); } catch { }
}

listener.Stop();
await serverTask;
Console.WriteLine("PROTOCOL SELF-TEST PASSED: HELLO, LIST, GET, HASH, AES-256-GCM AAD, SHA-256 och inkrementell kontroll.");
return;

async Task RunServerAsync(TcpListener server, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        TcpClient socket;
        try { socket = await server.AcceptTcpClientAsync(ct); }
        catch (OperationCanceledException) { break; }
        catch (ObjectDisposedException) { break; }
        catch (SocketException) when (!server.Server.IsBound || ct.IsCancellationRequested) { break; }
        _ = Task.Run(() => HandleClientAsync(socket, ct), ct);
    }
}

async Task HandleClientAsync(TcpClient socket, CancellationToken ct)
{
    using (socket)
    using (NetworkStream stream = socket.GetStream())
    {
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await WriteLineAsync(stream, $"NONCE {nonce}", ct);
        string request = await ReadLineAsync(stream, ct);
        string[] parts = request.Split(' ', 3, StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != "AUTH") throw new InvalidDataException("Ogiltig AUTH-ram");
        string command = parts[2];
        string expectedSignature = HmacHex(PairingCode, nonce + "\n" + command);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[1].ToLowerInvariant()), Encoding.ASCII.GetBytes(expectedSignature)))
            throw new InvalidDataException("Fel HMAC i sjalvtest");

        byte[] sessionKey = DeriveSessionKey(nonce);
        if (command == "HELLO")
        {
            await WriteEncryptedTextAsync(stream, sessionKey, command, "MOBILE_MTP_BACKUP_COMPANION/0.11\n", ct);
        }
        else if (command == "LIST")
        {
            string body = $"{MediaId}\tselftest.txt\t{MediaBytes.LongLength}\t{ModifiedUnix}\ttext/plain\tDCIM/SelfTest/\nEND\n";
            await WriteEncryptedTextAsync(stream, sessionKey, command, body, ct);
        }
        else if (command == $"GET {MediaId}")
        {
            await WriteEncryptedMediaAsync(stream, sessionKey, command, MediaBytes, ct);
        }
        else if (command == $"HASH {MediaId}")
        {
            string hash = Convert.ToHexString(SHA256.HashData(MediaBytes)).ToLowerInvariant() + "\n";
            await WriteEncryptedTextAsync(stream, sessionKey, command, hash, ct);
        }
        else
        {
            await WriteLineAsync(stream, "ERROR unknown-command", ct);
        }
    }
}

byte[] DeriveSessionKey(string nonce)
{
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(PairingCode));
    return hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ProtocolVersion}\nsession\n{nonce}"));
}

string HmacHex(string key, string text)
{
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
    return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

byte[] TextAad(string context, string header) => Encoding.UTF8.GetBytes($"{ProtocolVersion}\ntext\n{context}\n{header}");
byte[] MediaAad(string context, string header, long chunkIndex, int plainLength) => Encoding.UTF8.GetBytes($"{ProtocolVersion}\nmedia\n{context}\n{header}\n{chunkIndex}\n{plainLength}");

async Task WriteEncryptedTextAsync(Stream stream, byte[] key, string context, string text, CancellationToken ct)
{
    byte[] plain = Encoding.UTF8.GetBytes(text);
    string header = $"ETEXT {plain.Length}";
    await WriteLineAsync(stream, header, ct);
    byte[] nonce = RandomNumberGenerator.GetBytes(12);
    byte[] cipher = new byte[plain.Length];
    byte[] tag = new byte[16];
    using var aes = new AesGcm(key, 16);
    aes.Encrypt(nonce, plain, cipher, tag, TextAad(context, header));
    await stream.WriteAsync(nonce, ct);
    await stream.WriteAsync(cipher, ct);
    await stream.WriteAsync(tag, ct);
    await stream.FlushAsync(ct);
}

async Task WriteEncryptedMediaAsync(Stream stream, byte[] key, string context, byte[] data, CancellationToken ct)
{
    const int chunkSize = 1024 * 1024;
    string header = $"EDATA {data.LongLength} {chunkSize}";
    await WriteLineAsync(stream, header, ct);
    using var aes = new AesGcm(key, 16);
    long chunkIndex = 0;
    int offset = 0;
    while (offset < data.Length)
    {
        int len = Math.Min(chunkSize, data.Length - offset);
        byte[] plain = data.AsSpan(offset, len).ToArray();
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[len];
        byte[] tag = new byte[16];
        aes.Encrypt(nonce, plain, cipher, tag, MediaAad(context, header, chunkIndex, len));
        byte[] lenBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, len);
        await stream.WriteAsync(lenBytes, ct);
        await stream.WriteAsync(nonce, ct);
        await stream.WriteAsync(cipher, ct);
        await stream.WriteAsync(tag, ct);
        offset += len;
        chunkIndex++;
    }
    await stream.FlushAsync(ct);
}

async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
{
    var bytes = new List<byte>();
    byte[] one = new byte[1];
    while (true)
    {
        int n = await stream.ReadAsync(one, ct);
        if (n == 0 || one[0] == (byte)'\n') break;
        if (one[0] != (byte)'\r') bytes.Add(one[0]);
        if (bytes.Count > 4096) throw new InvalidDataException("For lang rad");
    }
    return Encoding.UTF8.GetString(bytes.ToArray());
}

async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
{
    byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
    await stream.WriteAsync(bytes, ct);
    await stream.FlushAsync(ct);
}

void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("SELF-TEST FAILED: " + message);
}
