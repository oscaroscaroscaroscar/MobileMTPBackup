using System.Security.Cryptography;
using System.Text.Json;

namespace MobileMTPBackup;

public sealed record BackupRecord(string RemotePath,string LocalPath,long Size,DateTime? SourceCreated,DateTime? SourceModified,DateTime BackedUpAt,string Sha256);

public sealed class BackupEngine
{
    private readonly MtpService _mtp;
    public BackupEngine(MtpService mtp) => _mtp = mtp;

    public async Task<BackupRecord> BackupOneFileAsync(MtpDeviceInfo device,MtpEntry source,string destinationRoot,bool preserveDates,bool verifySha256,Action<string> log)
    {
        if (source.IsDirectory) throw new ArgumentException("Källan är en mapp.");
        Directory.CreateDirectory(destinationRoot);
        string safeName = SanitizeFileName(source.Name);
        string localPath = GetUniquePath(Path.Combine(destinationRoot, safeName));
        string tempPath = localPath + ".partial";
        try
        {
            log($"Kopierar {source.FullName}");
            await Task.Run(() => _mtp.DownloadFile(device, source.FullName, tempPath));
            long size = new FileInfo(tempPath).Length;
            if (source.Length is long expected && expected >= 0 && size != expected) throw new IOException($"Storleken stämmer inte. Telefon: {expected} byte, backup: {size} byte.");
            File.Move(tempPath, localPath);
            DateTime? preferredDate = source.DateCreated ?? source.DateModified;
            if (preserveDates && preferredDate is DateTime dt)
            {
                File.SetCreationTime(localPath, dt);
                File.SetLastWriteTime(localPath, source.DateModified ?? dt);
            }
            string hash = verifySha256 ? await Sha256Async(localPath) : "NOT_CHECKED";
            log($"Klar: {localPath}");
            return new(source.FullName, localPath, size, source.DateCreated, source.DateModified, DateTime.Now, hash);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    public static async Task AppendManifestAsync(string root, BackupRecord record)
    {
        Directory.CreateDirectory(root);
        await File.AppendAllTextAsync(Path.Combine(root,"backup-manifest.jsonl"), JsonSerializer.Serialize(record)+Environment.NewLine);
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var input=File.OpenRead(path);
        using var sha=SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(input));
    }

    private static string SanitizeFileName(string name)
    {
        foreach(char c in Path.GetInvalidFileNameChars()) name=name.Replace(c,'_');
        return string.IsNullOrWhiteSpace(name) ? "unnamed-file" : name;
    }

    private static string GetUniquePath(string path)
    {
        if(!File.Exists(path)) return path;
        string dir=Path.GetDirectoryName(path)!; string stem=Path.GetFileNameWithoutExtension(path); string ext=Path.GetExtension(path);
        for(int i=2;;i++){ string candidate=Path.Combine(dir,$"{stem} ({i}){ext}"); if(!File.Exists(candidate)) return candidate; }
    }
}
