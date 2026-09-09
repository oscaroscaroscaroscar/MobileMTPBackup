using System.ComponentModel;
using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = AppSettingsStore.Load();
        DestinationTextBox.Text = settings.DestinationPath;

        if (!string.IsNullOrWhiteSpace(settings.LastDeviceName))
        {
            foreach (var item in DeviceComboBox.Items)
            {
                if (item is MtpDeviceInfo device && string.Equals(device.Name, settings.LastDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    DeviceComboBox.SelectedItem = item;
                    Log("Återställde senast använda telefon: " + device.Name);
                    break;
                }
            }
        }

        Log("Inställningar laddade. Backupmål: " + DestinationTextBox.Text);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            string destination = string.IsNullOrWhiteSpace(DestinationTextBox.Text)
                ? AppSettings.Default.DestinationPath
                : DestinationTextBox.Text.Trim();
            AppSettingsStore.Save(new AppSettings(destination, SelectedDevice?.Name));
        }
        catch (Exception ex)
        {
            Log("INSTÄLLNINGAR VARNING: kunde inte spara inställningar. " + ex.GetBaseException().Message);
        }
    }
}
