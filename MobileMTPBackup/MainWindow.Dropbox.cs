using System.IO;
using System.Text.Json;
using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private void UseDropbox_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? dropboxRoot = FindDropboxRoot();
            if (dropboxRoot is null)
            {
                MessageBox.Show(
                    "Dropbox for desktop hittades inte.\n\nStarta Dropbox och logga in. Tryck sedan DROPBOX igen. Du kan också välja Dropbox-mappen manuellt med Välj mapp.",
                    "DROPBOX",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Log("DROPBOX: ingen lokal Dropbox-mapp hittades.");
                return;
            }

            string target = Path.Combine(dropboxRoot, "MobileMTPBackup");
            Directory.CreateDirectory(target);
            ValidateWritableTarget(target);

            DestinationTextBox.Text = target;
            BackupStatusText.Text = "Dropbox valt och skrivtestat som backupmål. Filer verifieras lokalt och synkas därefter av Dropbox for desktop.";
            Log($"DROPBOX: synkad rot vald: {dropboxRoot}");
            Log("DROPBOX: backupmål valt och skrivtest godkänt: " + target);
            string? report = TryWriteDropboxTargetDiagnostic(dropboxRoot, target);
            if (report is not null) Log("DROPBOX DIAGNOSTIK SPARAD: " + report);

            MessageBox.Show(
                "Dropbox är valt som backupmål:\n\n" + target +
                "\n\nSynkad Dropbox-rot:\n" + dropboxRoot +
                "\n\nSkrivtest: GODKÄNT.\nBackupen går Mobil → dator → Dropbox. SHA-256 och .partial-säkerheten används innan filen färdigställs i Dropbox-mappen." +
                (report is null ? "" : "\n\nDiagnostik:\n" + report),
                "DROPBOX KLART",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log("DROPBOX FEL: " + ex.GetBaseException().Message);
            MessageBox.Show("Kunde inte använda Dropbox som backupmål.\n\n" + ex.GetBaseException().Message +
                "\n\nKontrollera att Dropbox är igång, att du är inloggad och att mappen är skrivbar.",
                "DROPBOX", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? FindDropboxRoot()
    {
        var candidates = new List<string>();
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(user, "Dropbox"));

        foreach (string config in GetDropboxInfoJsonCandidates())
        {
            try
            {
                if (!File.Exists(config)) continue;
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(config));
                foreach (JsonProperty account in doc.RootElement.EnumerateObject())
                {
                    if (account.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!account.Value.TryGetProperty("path", out JsonElement pathElement)) continue;
                    string? path = pathElement.GetString();
                    if (!string.IsNullOrWhiteSpace(path)) candidates.Insert(0, path);
                }
            }
            catch { }
        }

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> GetDropboxInfoJsonCandidates()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(local, "Dropbox", "info.json");
        yield return Path.Combine(roaming, "Dropbox", "info.json");
    }

    private string? TryWriteDropboxTargetDiagnostic(string dropboxRoot, string target)
    {
        try
        {
            string folder = Path.Combine(target, "_diagnostics");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"dropbox-target-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            string version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown";
            string volumeLabel = "unknown";
            long availableBytes = -1;
            try
            {
                string? root = Path.GetPathRoot(dropboxRoot);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady)
                    {
                        volumeLabel = drive.VolumeLabel;
                        availableBytes = drive.AvailableFreeSpace;
                    }
                }
            }
            catch { }

            string text = $"Mobile MTP Backup Dropbox target diagnostic\n" +
                          $"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n" +
                          $"App version: {version}\n" +
                          $"Dropbox root: {dropboxRoot}\n" +
                          $"Backup target: {target}\n" +
                          $"Volume label: {volumeLabel}\n" +
                          $"Available bytes: {availableBytes}\n" +
                          "Dropbox credentials/tokens: NOT USED OR INCLUDED\n";
            File.WriteAllText(path, text);
            return path;
        }
        catch (Exception reportEx)
        {
            Log("Kunde inte spara Dropbox-måldiagnostik: " + reportEx.GetBaseException().Message);
            return null;
        }
    }
}
