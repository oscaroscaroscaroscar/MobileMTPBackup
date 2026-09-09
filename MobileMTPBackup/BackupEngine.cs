using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MobileMTPBackup;

public sealed record BackupRecord(string RemotePath,string LocalPath,long Size,DateTime? SourceCreated,DateTime? SourceModified,DateTime BackedUpAt,string Sha256);
public sealed record FolderBackupResult(int FilesCopied,int FilesSkipped,int FilesFailed,long BytesCopied);

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
            log($"1/5 Hämtar från telefon: {source.FullName}");
            await Task.Run(() => _mtp.DownloadFile(device, source.FullName, tempPath));

            log("2/5 Kontrollerar filstorlek");
            long size = new FileInfo(tempPath).Length;
            if (source.Length is long expected && expected >= 0 && size != expected)
                throw new IOException($"Storleken stämmer inte. Telefon: {expected} byte, backup: {size} byte.");

            string hash = "NOT_CHECKED";
            if (verifySha256)
            {
                log("3/5 Beräknar SHA-256");
                hash = await Sha256Async(tempPath);
            }
            else
            {
                log("3/5 SHA-256 hoppades över");
            }

            log("4/5 Slutför filen");
            File.Move(tempPath, localPath);

            if (preserveDates)
            {
                try
                {
                    var created = ValidDate(source.DateCreated ?? source.DateModified);
                    var modified = ValidDate(source.DateModified ?? source.DateCreated);
                    if (created is DateTime c) File.SetCreationTime(localPath, c);
                    if (modified is DateTime m) File.SetLastWriteTime(localPath, m);
                    if (created is null && modified is null)
                        log("Datum saknas från MTP – filen sparades ändå. EXIF-fallback läggs till senare.");
                    else
                        log("Originaldatum bevarat när giltigt datum fanns.");
                }
                catch (Exception dateEx)
                {
                    log("VARNING: kunde inte sätta originaldatum, men filen är sparad. " + dateEx.GetBaseException().Message);
                }
            }

            log($"5/5 Klar: {localPath}");
            return new(source.FullName, localPath, size, ValidDate(source.DateCreated), ValidDate(source.DateModified), DateTime.Now, hash);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public async Task<FolderBackupResult> BackupFolderRecursiveAsync(
        MtpDeviceInfo device,
        string remoteFolderPath,
        string destinationRoot,
        bool preserveDates,
        bool verifySha256,
        bool incremental,
        Action<string> log,
        Action<int,int,string>? progress = null)
    {
        Directory.CreateDirectory(destinationRoot);
        var files = new List<(MtpEntry Entry,string RelativeFolder)>();
        CollectFiles(device, remoteFolderPath, "", files, log);

        int copied = 0, skipped = 0, failed = 0;
        long bytesCopied = 0;
        int total = files.Count;
        log($"Hel-mapp backup: {total} fil(er) hittades i {remoteFolderPath}.");

        for (int index = 0; index < total; index++)
        {
            var item = files[index];
            string localDir = string.IsNullOrWhiteSpace(item.RelativeFolder)
                ? destinationRoot
                : Path.Combine(destinationRoot, item.RelativeFolder);
            Directory.CreateDirectory(localDir);
            string expectedPath = Path.Combine(localDir, SanitizeFileName(item.Entry.Name));

            progress?.Invoke(index, total, item.Entry.FullName);

            if (incremental && File.Exists(expectedPath) && item.Entry.Length is long expectedLength)
            {
                try
                {
                    if (new FileInfo(expectedPath).Length == expectedLength)
                    {
                        skipped++;
                        log($"HOPPAR ÖVER oförändrad fil: {item.Entry.FullName}");
                        progress?.Invoke(index + 1, total, item.Entry.FullName);
                        continue;
                    }
                }
                catch { }
            }

            try
            {
                var rec = await BackupOneFileAsync(device, item.Entry, localDir, preserveDates, verifySha256, log);
                bytesCopied += rec.Size;
                copied++;
                try { await AppendManifestAsync(destinationRoot, rec); }
                catch (Exception mex) { log("MANIFEST VARNING: " + mex.GetBaseException().Message); }
            }
            catch (Exception ex)
            {
                failed++;
                log($"FILFEL: {item.Entry.FullName}: {ex.GetBaseException().Message}");
            }

            progress?.Invoke(index + 1, total, item.Entry.FullName);
        }

        return new FolderBackupResult(copied, skipped, failed, bytesCopied);
    }

    private void CollectFiles(MtpDeviceInfo device,string remoteFolder,string relativeFolder,List<(MtpEntry Entry,string RelativeFolder)> files,Action<string> log)
    {
        var entries = _mtp.GetEntries(device, remoteFolder);
        foreach (var entry in entries)
        {
            if (!entry.IsDirectory)
            {
                files.Add((entry, relativeFolder));
                continue;
            }

            string safeFolder = SanitizeFileName(entry.Name);
            string childRelative = string.IsNullOrWhiteSpace(relativeFolder)
                ? safeFolder
                : Path.Combine(relativeFolder, safeFolder);
            log($"Läser undermapp: {entry.FullName}");
            CollectFiles(device, entry.FullName, childRelative, files, log);
        }
    }

    public static async Task AppendManifestAsync(string root, BackupRecord record)
    {
        Directory.CreateDirectory(root);
        await File.AppendAllTextAsync(Path.Combine(root,"backup-manifest.jsonl"), JsonSerializer.Serialize(record)+Environment.NewLine);
    }

    private static DateTime? ValidDate(DateTime? value)
    {
        if (value is not DateTime dt) return null;
        return dt.Year >= 1970 && dt <= DateTime.Now.AddDays(2) ? dt : null;
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
