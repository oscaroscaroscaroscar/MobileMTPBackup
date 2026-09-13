using System.IO;
using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private void UseGoogleDrive_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? driveRoot = FindGoogleDriveRoot();
            if (driveRoot is null)
            {
                MessageBox.Show(
                    "Google Drive for desktop hittades inte.\n\nStarta Google Drive for desktop och logga in. Tryck sedan GOOGLE DRIVE igen. Du kan också välja Google Drive-mappen manuellt med Välj mapp.",
                    "GOOGLE DRIVE",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Log("GOOGLE DRIVE: ingen monterad/synkad Google Drive-mapp hittades.");
                return;
            }

            string target = Path.Combine(driveRoot, "MobileMTPBackup");
            Directory.CreateDirectory(target);
            ValidateWritableTarget(target);

            DestinationTextBox.Text = target;
            BackupStatusText.Text = "Google Drive valt och skrivtestat som backupmål. Filer verifieras lokalt och synkas därefter av Google Drive for desktop.";
            Log("GOOGLE DRIVE: backupmål valt och skrivtest godkänt: " + target);
            MessageBox.Show(
                "Google Drive är valt som backupmål:\n\n" + target +
                "\n\nSkrivtest: GODKÄNT.\nBackupen går Mobil → dator → Google Drive. SHA-256 och .partial-säkerheten används innan filen färdigställs i Drive-mappen.",
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

    private static string? FindGoogleDriveRoot()
    {
        var candidates = new List<string>();
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(user, "Google Drive"));
        candidates.Add(Path.Combine(user, "My Drive"));
        candidates.Add(Path.Combine(user, "Min enhet"));

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                string root = drive.RootDirectory.FullName;
                if (drive.VolumeLabel.Contains("Google Drive", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Insert(0, Path.Combine(root, "My Drive"));
                    candidates.Insert(0, Path.Combine(root, "Min enhet"));
                    candidates.Insert(0, root);
                }
                candidates.Add(Path.Combine(root, "My Drive"));
                candidates.Add(Path.Combine(root, "Min enhet"));
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
}
