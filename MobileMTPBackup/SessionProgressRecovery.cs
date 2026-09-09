using System.IO;
using System.Text.Json;

namespace MobileMTPBackup;

public sealed record RecoveredSessionProgress(int FilesCopied,long BytesCopied);

public static class SessionProgressRecovery
{
    public static async Task<RecoveredSessionProgress> RecoverAsync(string destinationRoot, DateTime startedAt)
    {
        if (!Directory.Exists(destinationRoot)) return new RecoveredSessionProgress(0,0);

        int copied = 0;
        long bytes = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string manifest in Directory.EnumerateFiles(destinationRoot,"backup-manifest.jsonl",SearchOption.AllDirectories))
        {
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(manifest); }
            catch { continue; }

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var rec = JsonSerializer.Deserialize<BackupRecord>(line);
                    if (rec is null || rec.BackedUpAt < startedAt) continue;
                    if (!seen.Add(rec.LocalPath)) continue;
                    if (!File.Exists(rec.LocalPath)) continue;
                    copied++;
                    bytes += Math.Max(0,rec.Size);
                }
                catch { }
            }
        }

        return new RecoveredSessionProgress(copied,bytes);
    }
}
