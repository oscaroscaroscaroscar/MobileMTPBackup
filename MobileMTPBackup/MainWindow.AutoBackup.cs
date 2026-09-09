using System.IO;
using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private static readonly HashSet<string> AutoMediaFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DCIM", "Pictures", "Movies", "Download", "Documents"
    };

    private async void AutoBackupMedia_Click(object sender, RoutedEventArgs e)
    {
        var d = SelectedDevice;
        if (d is null) { Log("Ingen telefon vald."); return; }
        if (_folderCts is not null) { Log("En backup kör redan."); return; }

        string basePath = DestinationTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(basePath)) { MessageBox.Show("Välj backupmapp först.", "AUTO BACKUP MEDIA"); return; }

        DateTime startedAt = DateTime.Now;
        string? reportRoot = null;
        _folderCts = new CancellationTokenSource();
        _isPaused = false;

        try
        {
            AutoBackupButton.IsEnabled = false;
            BackupFolderButton.IsEnabled = false;
            PauseButton.IsEnabled = true;
            ResumeButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            BackupProgressBar.Value = 0;
            BackupStatusText.Text = "Auto-backup: söker DCIM, Pictures, Movies, Download och Documents...";

            var mediaFolders = await Task.Run(() => DiscoverAutoMediaFolders(d, _folderCts.Token));
            if (mediaFolders.Count == 0)
                throw new InvalidOperationException("Hittade inga standardmappar för media/dokument på telefonen.");

            Log("AUTO MEDIA: hittade " + string.Join(", ", mediaFolders.Select(x => x.FullName)));

            long estimatedBytes = 0;
            int estimatedFiles = 0;
            foreach (var folder in mediaFolders)
            {
                var scan = await Task.Run(() => ScanAutoMediaFolder(d, folder.FullName, _folderCts.Token));
                estimatedBytes += scan.Bytes;
                estimatedFiles += scan.Files;
                if (scan.InaccessibleFolders > 0)
                    Log($"AUTO MEDIA VARNING: {folder.FullName} har {scan.InaccessibleFolders} otillgängliga mappar som hoppas över i förkontrollen.");
            }

            EnsureFreeSpace(basePath, estimatedBytes);
            Log($"AUTO MEDIA FÖRKONTROLL OK: cirka {estimatedFiles} filer, {FormatBytes(estimatedBytes)}.");

            string autoRoot = Path.Combine(basePath, Sanitize(d.Name), "AutoMedia");
            Directory.CreateDirectory(autoRoot);
            reportRoot = autoRoot;

            int copied = 0, skipped = 0, failed = 0, completedFolders = 0;
            long bytesCopied = 0;

            foreach (var folder in mediaFolders)
            {
                _folderCts.Token.ThrowIfCancellationRequested();
                while (_isPaused)
                {
                    await Task.Delay(250, _folderCts.Token);
                }

                string localRoot = Path.Combine(autoRoot, RemotePathToLocal(folder.FullName));
                Directory.CreateDirectory(localRoot);
                int folderNumber = completedFolders + 1;
                BackupStatusText.Text = $"Auto-backup {folderNumber}/{mediaFolders.Count}: {folder.FullName}";
                Log($"AUTO MEDIA BACKUP: {folder.FullName} -> {localRoot}");

                var result = await _backup.BackupFolderRecursiveAsync(
                    d,
                    folder.FullName,
                    localRoot,
                    PreserveDatesCheckBox.IsChecked == true,
                    VerifyCheckBox.IsChecked == true,
                    IncrementalCheckBox.IsChecked == true,
                    Log,
                    (done, total, path) => Dispatcher.Invoke(() =>
                    {
                        double withinFolder = total == 0 ? 0 : (double)done / total;
                        BackupProgressBar.Value = ((folderNumber - 1) + withinFolder) / mediaFolders.Count * 100;
                        BackupStatusText.Text = $"Auto-backup {folderNumber}/{mediaFolders.Count} – {done}/{total}: {path}";
                    }),
                    _folderCts.Token,
                    () => _isPaused);

                copied += result.FilesCopied;
                skipped += result.FilesSkipped;
                failed += result.FilesFailed;
                bytesCopied += result.BytesCopied;
                completedFolders++;
            }

            BackupProgressBar.Value = 100;
            BackupStatusText.Text = $"AUTO BACKUP KLAR: {copied} kopierade, {skipped} hoppades över, {failed} fel.";
            Log($"AUTO MEDIA KLAR: mappar={completedFolders}, kopierade={copied}, överhoppade={skipped}, fel={failed}, byte={bytesCopied}");

            try
            {
                string report = await SessionReportWriter.WriteAsync(autoRoot, new BackupSessionReport(
                    AppVersion, d.Name, string.Join("; ", mediaFolders.Select(x => x.FullName)), autoRoot,
                    startedAt, DateTime.Now, copied, skipped, failed, bytesCopied, false, null));
                Log("SESSIONSRAPPORT AUTO MEDIA: " + report);
            }
            catch (Exception rex) { Log("RAPPORT VARNING: " + rex.GetBaseException().Message); }
        }
        catch (OperationCanceledException)
        {
            BackupStatusText.Text = "AUTO BACKUP AVBRUTEN av användaren.";
            Log("AUTO MEDIA BACKUP AVBRUTEN.");
            if (reportRoot is not null)
            {
                try
                {
                    string report = await SessionReportWriter.WriteAsync(reportRoot, new BackupSessionReport(
                        AppVersion, d.Name, "AutoMedia", reportRoot, startedAt, DateTime.Now, 0, 0, 0, 0, true, null));
                    Log("SESSIONSRAPPORT AUTO MEDIA: " + report);
                }
                catch (Exception rex) { Log("RAPPORT VARNING: " + rex.GetBaseException().Message); }
            }
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = "AUTO BACKUP misslyckades – se loggen.";
            Log("AUTO MEDIA FEL: " + ex);
            MessageBox.Show(ex.ToString(), "AUTO BACKUP MEDIA FEL", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isPaused = false;
            _folderCts?.Dispose();
            _folderCts = null;
            AutoBackupButton.IsEnabled = true;
            BackupFolderButton.IsEnabled = true;
            PauseButton.IsEnabled = false;
            ResumeButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
        }
    }

    private List<MtpEntry> DiscoverAutoMediaFolders(MtpDeviceInfo device, CancellationToken token)
    {
        var found = new Dictionary<string, MtpEntry>(StringComparer.OrdinalIgnoreCase);
        var rootEntries = _mtp.GetRootEntries(device);

        void AddMatches(IEnumerable<MtpEntry> entries)
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.IsDirectory && AutoMediaFolderNames.Contains(entry.Name))
                    found[entry.FullName] = entry;
            }
        }

        AddMatches(rootEntries);
        foreach (var rootDir in rootEntries.Where(x => x.IsDirectory))
        {
            token.ThrowIfCancellationRequested();
            try { AddMatches(_mtp.GetEntries(device, rootDir.FullName)); }
            catch { /* storage roots that cannot be opened are simply skipped */ }
        }

        return found.Values
            .OrderBy(x => Array.IndexOf(new[] { "DCIM", "Pictures", "Movies", "Download", "Documents" }, x.Name), Comparer<int>.Default)
            .ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private PreviewInfo ScanAutoMediaFolder(MtpDeviceInfo device, string remoteFolder, CancellationToken token)
    {
        long bytes = 0;
        int files = 0, folders = 0, inaccessible = 0;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(string path)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(path)) return;
            IReadOnlyList<MtpEntry> entries;
            try { entries = _mtp.GetEntries(device, path); }
            catch { inaccessible++; return; }

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.IsDirectory) { folders++; Walk(entry.FullName); }
                else { files++; if (entry.Length is long len && len > 0) bytes += len; }
            }
        }

        Walk(remoteFolder);
        return new PreviewInfo(files, folders, bytes, inaccessible);
    }

    private static string RemotePathToLocal(string remotePath)
    {
        var parts = remotePath.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(Sanitize).ToArray();
        return parts.Length == 0 ? "Telefonrot" : Path.Combine(parts);
    }
}
