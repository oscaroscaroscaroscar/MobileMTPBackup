using System.ComponentModel;
using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = AppSettingsStore.Load();
        DestinationTextBox.Text = settings.DestinationPath;
        ConnectionModeComboBox.SelectedIndex = string.Equals(settings.ConnectionMode, "Wi-Fi", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        WifiHostTextBox.Text = settings.WifiHost;
        WifiPortTextBox.Text = settings.WifiPort.ToString();

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
            int port = int.TryParse(WifiPortTextBox.Text.Trim(), out var parsedPort) && parsedPort is >= 1 and <= 65535 ? parsedPort : 8765;
            AppSettingsStore.Save(new AppSettings(
                destination,
                SelectedDevice?.Name,
                IsWifiMode ? "Wi-Fi" : "USB/MTP",
                WifiHostTextBox.Text.Trim(),
                port));
        }
        catch (Exception ex)
        {
            Log("INSTÄLLNINGAR VARNING: kunde inte spara inställningar. " + ex.GetBaseException().Message);
        }
    }
}
