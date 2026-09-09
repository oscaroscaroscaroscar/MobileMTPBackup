using System.Net.Sockets;
using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private async void TestWifi_Click(object sender, RoutedEventArgs e)
    {
        string host = WifiHostTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show("Ange telefonens IP-adress eller värdnamn.", "WI-FI TEST");
            return;
        }

        if (!int.TryParse(WifiPortTextBox.Text.Trim(), out int port) || port is < 1 or > 65535)
        {
            MessageBox.Show("Ange en giltig port mellan 1 och 65535.", "WI-FI TEST");
            return;
        }

        WifiStatusText.Text = "Testar Wi-Fi-anslutning...";
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, cts.Token);
            WifiStatusText.Text = $"Wi-Fi OK: {host}:{port} svarar.";
            Log($"WI-FI TEST OK: {host}:{port} svarar på TCP.");
        }
        catch (Exception ex)
        {
            WifiStatusText.Text = "Ingen Wi-Fi-tjänst svarar ännu.";
            Log("WI-FI TEST: " + ex.GetBaseException().Message);
            MessageBox.Show(
                "Datorn nådde inte någon backup-tjänst på telefonen ännu.\n\n" +
                "v5.16 lägger grunden för trådlös backup. Själva Android-kompanjonen/filprotokollet byggs i nästa steg.",
                "WI-FI TEST",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private bool IsWifiMode => ConnectionModeComboBox.SelectedIndex == 1;
}
