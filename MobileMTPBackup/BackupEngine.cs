using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
            else log("3/5 SHA-256 hoppades över");

            log("4/5 Slutför filen");
            File.Move(tempPath, localPath);

            if (preserveDates)
            {
                try
                {
                    var created = ValidDate(source.DateCreated ?? source.DateModified);
                    var modified = ValidDate(source.DateModified ?? source.DateCreated);
                    if (created is null && modified is null)
                    {
                        var exif = TryReadExifDateTimeOriginal(localPath);
                        if (exif is DateTime exifDate)
                        {
                            created = exifDate;
                            modified = exifDate;
                            log($"EXIF DateTimeOriginal används: {exifDate:yyyy-MM-dd HH:mm:ss}");
                        }
                        else log("Inget giltigt MTP- eller EXIF-datum hittades – filens backupdatum behålls.");
                    }
                    else log("Originaldatum från MTP används.");
                    if (created is DateTime c) File.SetCreationTime(localPath, c);
                    if (modified is DateTime m) File.SetLastWriteTime(localPath, m);
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

    public async Task<FolderBackupResult> BackupFolderRecursiveAsync(MtpDeviceInfo device,string remoteFolderPath,string destinationRoot,bool preserveDates,bool verifySha256,bool incremental,Action<string> log,Action<int,int,string>? progress = null)
    {
        Directory.CreateDirectory(destinationRoot);
        var files = new List<(MtpEntry Entry,string RelativeFolder)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFiles(device, remoteFolderPath, "", files, visited, log);

        var manifestIndex = incremental
            ? await LoadManifestIndexAsync(destinationRoot, log)
            : new Dictionary<string, BackupRecord>(StringComparer.OrdinalIgnoreCase);

        int copied = 0, skipped = 0, failed = 0;
        long bytesCopied = 0;
        int total = files.Count;
        log($"Hel-mapp backup: {total} fil(er) hittades i {remoteFolderPath}.");

        for (int index = 0; index < total; index++)
        {
            var item = files[index];
            string localDir = string.IsNullOrWhiteSpace(item.RelativeFolder) ? destinationRoot : Path.Combine(destinationRoot, item.RelativeFolder);
            Directory.CreateDirectory(localDir);
            progress?.Invoke(index, total, item.Entry.FullName);

            if (incremental && await CanSkipIncrementalAsync(item.Entry, manifestIndex, verifySha256, log))
            {
                skipped++;
                progress?.Invoke(index + 1, total, item.Entry.FullName);
                continue;
            }

            try
            {
                var rec = await BackupOneFileAsync(device, item.Entry, localDir, preserveDates, verifySha256, log);
                bytesCopied += rec.Size;
                copied++;
                manifestIndex[rec.RemotePath] = rec;
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

    private async Task<bool> CanSkipIncrementalAsync(MtpEntry entry,Dictionary<string,BackupRecord> manifestIndex,bool verifySha256,Action<string> log)
    {
        if (!manifestIndex.TryGetValue(entry.FullName, out var old)) return false;
        if (entry.Length is long sourceSize && sourceSize >= 0 && old.Size != sourceSize) return false;
        if (string.IsNullOrWhiteSpace(old.LocalPath) || !File.Exists(old.LocalPath)) return false;
        try
        {
            if (new FileInfo(old.LocalPath).Length != old.Size) return false;
            if (verifySha256)
            {
                if (string.IsNullOrWhiteSpace(old.Sha256) || old.Sha256 == "NOT_CHECKED") return false;
                string currentHash = await Sha256Async(old.LocalPath);
                if (!string.Equals(currentHash, old.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    log($"INKREMENTELL KONTROLL: hash skiljer sig, kopierar igen: {entry.FullName}");
                    return false;
                }
            }
            log($"HOPPAR ÖVER verifierad oförändrad fil: {entry.FullName}");
            return true;
        }
        catch (Exception ex)
        {
            log($"INKREMENTELL VARNING: kunde inte verifiera tidigare backup, kopierar igen: {ex.GetBaseException().Message}");
            return false;
        }
    }

    private void CollectFiles(MtpDeviceInfo device,string remoteFolder,string relativeFolder,List<(MtpEntry Entry,string RelativeFolder)> files,HashSet<string> visited,Action<string> log)
    {
        if (!visited.Add(remoteFolder))
        {
            log($"VARNING: mappcykel upptäckt och hoppades över: {remoteFolder}");
            return;
        }

        IReadOnlyList<MtpEntry> entries;
        try { entries = _mtp.GetEntries(device, remoteFolder); }
        catch (Exception ex)
        {
            log($"VARNING: kunde inte läsa mappen {remoteFolder}; fortsätter med övriga mappar. {ex.GetBaseException().Message}");
            return;
        }

        foreach (var entry in entries)
        {
            if (!entry.IsDirectory) { files.Add((entry, relativeFolder)); continue; }
            string safeFolder = SanitizeFileName(entry.Name);
            string childRelative = string.IsNullOrWhiteSpace(relativeFolder) ? safeFolder : Path.Combine(relativeFolder, safeFolder);
            log($"Läser undermapp: {entry.FullName}");
            CollectFiles(device, entry.FullName, childRelative, files, visited, log);
        }
    }

    private static async Task<Dictionary<string,BackupRecord>> LoadManifestIndexAsync(string root,Action<string> log)
    {
        var index = new Dictionary<string,BackupRecord>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(root,"backup-manifest.jsonl");
        if (!File.Exists(path)) return index;
        try
        {
            foreach (string line in await File.ReadAllLinesAsync(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var rec = JsonSerializer.Deserialize<BackupRecord>(line);
                    if (rec is not null) index[rec.RemotePath] = rec;
                }
                catch { }
            }
            log($"Inkrementellt manifest laddat: {index.Count} filpost(er).");
        }
        catch (Exception ex)
        {
            log("MANIFEST VARNING: tidigare manifest kunde inte läsas. " + ex.GetBaseException().Message);
        }
        return index;
    }

    public static async Task AppendManifestAsync(string root, BackupRecord record)
    {
        Directory.CreateDirectory(root);
        await File.AppendAllTextAsync(Path.Combine(root,"backup-manifest.jsonl"), JsonSerializer.Serialize(record)+Environment.NewLine);
    }

    private static DateTime? TryReadExifDateTimeOriginal(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".jpg" and not ".jpeg") return null;
        try
        {
            byte[] data = File.ReadAllBytes(path);
            for (int p = 0; p <= data.Length - 6; p++)
            {
                if (data[p] != (byte)'E' || data[p+1] != (byte)'x' || data[p+2] != (byte)'i' || data[p+3] != (byte)'f' || data[p+4] != 0 || data[p+5] != 0) continue;
                int tiff = p + 6;
                if (tiff + 8 > data.Length) continue;
                bool little;
                if (data[tiff] == (byte)'I' && data[tiff+1] == (byte)'I') little = true;
                else if (data[tiff] == (byte)'M' && data[tiff+1] == (byte)'M') little = false;
                else continue;
                if (ReadU16(data,tiff+2,little) != 42) continue;
                uint ifd0Off = ReadU32(data,tiff+4,little);
                int ifd0 = checked(tiff + (int)ifd0Off);
                uint? exifIfdOff = FindLongTagValue(data,tiff,ifd0,0x8769,little);
                if (exifIfdOff is null) continue;
                int exifIfd = checked(tiff + (int)exifIfdOff.Value);
                string? value = FindAsciiTagValue(data,tiff,exifIfd,0x9003,little);
                if (value is null) continue;
                if (DateTime.TryParseExact(value.Trim('\0',' '),"yyyy:MM:dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var dt) && ValidDate(dt) is not null)
                    return dt;
            }
        }
        catch { }
        return null;
    }

    private static uint? FindLongTagValue(byte[] data,int tiff,int ifd,int tag,bool little)
    {
        if (ifd < 0 || ifd + 2 > data.Length) return null;
        int count = ReadU16(data,ifd,little);
        for (int i=0;i<count;i++)
        {
            int e = ifd + 2 + i*12;
            if (e + 12 > data.Length) return null;
            if (ReadU16(data,e,little)==tag && ReadU16(data,e+2,little)==4 && ReadU32(data,e+4,little)>=1)
                return ReadU32(data,e+8,little);
        }
        return null;
    }

    private static string? FindAsciiTagValue(byte[] data,int tiff,int ifd,int tag,bool little)
    {
        if (ifd < 0 || ifd + 2 > data.Length) return null;
        int count = ReadU16(data,ifd,little);
        for (int i=0;i<count;i++)
        {
            int e = ifd + 2 + i*12;
            if (e + 12 > data.Length) return null;
            if (ReadU16(data,e,little)!=tag || ReadU16(data,e+2,little)!=2) continue;
            uint len = ReadU32(data,e+4,little);
            if (len==0 || len>128) return null;
            int pos = len<=4 ? e+8 : checked(tiff + (int)ReadU32(data,e+8,little));
            if (pos < 0 || pos + len > data.Length) return null;
            return Encoding.ASCII.GetString(data,pos,(int)len);
        }
        return null;
    }

    private static ushort ReadU16(byte[] data,int pos,bool little)
    {
        var span = data.AsSpan(pos,2);
        return little ? BinaryPrimitives.ReadUInt16LittleEndian(span) : BinaryPrimitives.ReadUInt16BigEndian(span);
    }

    private static uint ReadU32(byte[] data,int pos,bool little)
    {
        var span = data.AsSpan(pos,4);
        return little ? BinaryPrimitives.ReadUInt32LittleEndian(span) : BinaryPrimitives.ReadUInt32BigEndian(span);
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
