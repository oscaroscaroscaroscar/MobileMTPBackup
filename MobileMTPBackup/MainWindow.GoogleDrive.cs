using System.IO;
using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private sealed record GoogleDriveTarget(string Path, string Kind);

    private void UseGoogleDrive_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            GoogleDriveTarget? drive = FindGoogleDriveRoot();
            if (drive is null)
            {
                MessageBox.Show(
                    "Google Drive for desktop hittades inte.\n\nStarta Google Drive for desktop och logga in. Tryck sedan GOOGLE DRIVE igen. Du kan också välja Google Drive-mappen manuellt med Välj mapp.",
                    "GOOGLE DRIVE",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Log("GOOGLE DRIVE: ingen monterad/synkad Google Drive-mapp hittades.");
                return;
            }

            string target = Path.Combine(drive.Path, "MobileMTPBackup");
            Directory.CreateDirectory(target);
            ValidateWritableTarget(target);

            DestinationTextBox.Text = target;
            BackupStatusText.Text = "Google Drive valt och skrivtestat som backupmål. En sanerad Drive-diagnostikrapport har också skapats.";
            Log($"GOOGLE DRIVE: synkad rot vald: {drive.Path} ({drive.Kind})");
            Log("GOOGLE DRIVE: backupmål valt och skrivtest godkänt: " + target);

            string? report = TryWriteGoogleDriveDiagnosticReport(drive, target);
            if (report is not null)
                Log("GOOGLE DRIVE DIAGNOSTIK SPARAD: " + report);

            MessageBox.Show(
                "Google Drive är valt som backupmål:\n\n" + target +
                "\n\nSynkad Drive-rot:\n" + drive.Path +
                "\nTyp: " + drive.Kind +
                "\n\nSkrivtest: GODKÄNT.\nBackupen går Mobil → dator → Google Drive. SHA-256 och .partial-säkerheten används innan filen färdigställs i Drive-mappen." +
                (report is null ? "" : "\n\nDiagnostikrapport:\n" + report),
                "GOOGLE DRIVE KLART",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log("GOOGLE DRIVE FEL: " + ex.GetBaseException().Message);
            MessageBox.Show("Kunde inte använda Google Drive som backupmål.\n\n" + ex.GetBaseException().Message +
                "\n\nKontrollera att Google Drive for desktop är igång, att du är inloggad och att mappen är skrivbar.",
                "GOOGLE DRIVE", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string? TryWriteGoogleDriveDiagnosticReport(GoogleDriveTarget drive, string target)
    {
        try
        {
            string folder = Path.Combine(target, "_diagnostics");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"google-drive-target-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            string appVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown";
            string freeSpace = "unknown";
            string volumeLabel = "unknown";

            try
            {
                string? root = Path.GetPathRoot(drive.Path);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    var info = new DriveInfo(root);
                    if (info.IsReady)
                    {
                        freeSpace = info.AvailableFreeSpace.ToString();
                        volumeLabel = info.VolumeLabel;
                    }
                }
            }
            catch { }

            string text =
                "Mobile MTP Backup Google Drive diagnostic\n" +
                $"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n" +
                $"App version: {appVersion}\n" +
                $"Detected root: {drive.Path}\n" +
                $"Root kind: {drive.Kind}\n" +
                $"Backup target: {target}\n" +
                $"Volume label: {volumeLabel}\n" +
                $"Available bytes: {freeSpace}\n" +
                "Write/read probe: SUCCESS\n" +
                "Google account details: NOT INCLUDED\n" +
                "OAuth tokens/credentials: NOT USED OR INCLUDED\n";
            File.WriteAllText(path, text);
            return path;
        }
        catch (Exception reportEx)
        {
            Log("Kunde inte spara Google Drive-diagnostikrapport: " + reportEx.GetBaseException().Message);
            return null;
        }
    }

    private static void ValidateWritableTarget(string target)
    {
        string probe = Path.Combine(target, $".mobilemtpbackup-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "MobileMTPBackup Google Drive write test");
            using var stream = new FileStream(probe, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0)
                throw new IOException("Skrivtestfilen kunde inte verifieras.");
        }
        finally
        {
            try
            {
                if (File.Exists(probe)) File.Delete(probe);
            }
            catch { }
        }
    }

    private static GoogleDriveTarget? FindGoogleDriveRoot()
    {
        var preferred = new List<GoogleDriveTarget>();
        var fallback = new List<GoogleDriveTarget>();
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        preferred.Add(new(Path.Combine(user, "Google Drive", "My Drive"), "My Drive"));
        preferred.Add(new(Path.Combine(user, "Google Drive", "Min enhet"), "Min enhet"));
        preferred.Add(new(Path.Combine(user, "My Drive"), "My Drive"));
        preferred.Add(new(Path.Combine(user, "Min enhet"), "Min enhet"));
        fallback.Add(new(Path.Combine(user, "Google Drive"), "Google Drive root fallback"));

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                string root = drive.RootDirectory.FullName;
                string myDrive = Path.Combine(root, "My Drive");
                string minEnhet = Path.Combine(root, "Min enhet");

                if (drive.VolumeLabel.Contains("Google Drive", StringComparison.OrdinalIgnoreCase))
                {
                    preferred.Insert(0, new(minEnhet, "Min enhet"));
                    preferred.Insert(0, new(myDrive, "My Drive"));
                    fallback.Insert(0, new(root, "Google Drive volume root fallback"));
                }
                else
                {
                    preferred.Add(new(myDrive, "My Drive"));
                    preferred.Add(new(minEnhet, "Min enhet"));
                }
            }
            catch { }
        }

        foreach (GoogleDriveTarget candidate in preferred
                     .Concat(fallback)
                     .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.First()))
        {
            try
            {
                if (Directory.Exists(candidate.Path)) return candidate;
            }
            catch { }
        }
        return null;
    }
}
