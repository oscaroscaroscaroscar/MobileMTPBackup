using System.Net.Sockets;
using System.Text;
using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private async void TestWifi_Click(object sender, RoutedEventArgs e)
    {
        string host=WifiHostTextBox.Text.Trim();
        if(string.IsNullOrWhiteSpace(host)){MessageBox.Show("Ange telefonens IP-adress eller värdnamn.","WI-FI TEST");return;}
        if(!int.TryParse(WifiPortTextBox.Text.Trim(),out int port)||port is <1 or >65535){MessageBox.Show("Ange en giltig port mellan 1 och 65535.","WI-FI TEST");return;}
        WifiStatusText.Text="Testar Wi-Fi-anslutning...";
        try
        {
            using var client=new TcpClient();using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host,port,cts.Token);
            using var stream=client.GetStream();byte[] hello=Encoding.UTF8.GetBytes("HELLO\n");await stream.WriteAsync(hello,cts.Token);await stream.FlushAsync(cts.Token);
            using var reader=new StreamReader(stream,Encoding.UTF8,false,1024,true);string? reply=await reader.ReadLineAsync(cts.Token);
            if(reply is null||!reply.StartsWith("MOBILE_MTP_BACKUP_COMPANION/",StringComparison.Ordinal))throw new IOException("Fel tjänst svarade på porten.");
            WifiStatusText.Text=$"Wi-Fi OK: {host}:{port} – Companion {reply.Split('/').Last()}.";Log($"WI-FI HANDSHAKE OK: {reply} från {host}:{port}.");
        }
        catch(Exception ex){WifiStatusText.Text="Ingen kompatibel Wi-Fi Companion svarar.";Log("WI-FI TEST: "+ex.GetBaseException().Message);MessageBox.Show("Kunde inte verifiera Mobile MTP Backup Companion.\n\n"+ex.GetBaseException().Message,"WI-FI TEST",MessageBoxButton.OK,MessageBoxImage.Information);}
    }

    private bool IsWifiMode=>ConnectionModeComboBox.SelectedIndex==1;
}
