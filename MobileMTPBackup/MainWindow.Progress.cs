using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private void BackupProgressBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        double value = Math.Clamp(e.NewValue, 0, 100);
        ProgressPercentText.Text = value >= 99.999 ? "100% - KLAR" : $"{value:0}%";
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
