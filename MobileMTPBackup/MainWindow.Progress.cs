using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private void BackupProgressBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        double value = Math.Clamp(e.NewValue, 0, 100);
        ProgressPercentText.Text = value >= 99.999 ? "100% - KLAR" : $"{value:0}%";
    }

    private void SetProgress(int done, int total)
    {
        double value = total <= 0 ? 0 : Math.Clamp((double)done / total * 100, 0, 100);
        BackupProgressBar.Value = value;
        ProgressPercentText.Text = value >= 99.999
            ? $"100% - KLAR • {done}/{Math.Max(done, total)} filer"
            : $"{value:0}% • {done}/{Math.Max(0, total)} filer";
    }

    private void ResetProgress()
    {
        BackupProgressBar.Value = 0;
        ProgressPercentText.Text = "0%";
    }

    private void CompleteProgress()
    {
        BackupProgressBar.Value = 100;
        ProgressPercentText.Text = "100% - KLAR";
    }
}
