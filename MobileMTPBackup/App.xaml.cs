using System.Windows.Threading;
using MobileMTPBackup.Services;
using System.Windows;

namespace MobileMTPBackup;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                string path = CrashLogger.Log(e.Exception);
                MessageBox.Show(
                    $"Ett oväntat fel inträffade. En kraschlogg sparades här:\n{path}",
                    "Mobile MTP Backup",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { }

            e.Handled = true;
        };
    }
}
