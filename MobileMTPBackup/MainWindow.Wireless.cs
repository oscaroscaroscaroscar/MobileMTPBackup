using System.IO;
using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private async void TestWifi_Click(object sender, RoutedEventArgs e)
    {
        string host=WifiHostTextBox.Text.Trim();
        if(string.IsNullOrWhiteSpace(host)){MessageBox.Show("Ange telefonens IP-adress eller värdnamn.","WI-FI TEST");return;}
        if(!int.TryParse(WifiPortTextBox.Text.Trim(),out int port)||port is <1 or >65535){MessageBox.Show("Ange en giltig port mellan 1 och 65535.","WI-FI TEST");return;}
        string pairing=WifiPairCodeTextBox.Text.Trim().ToLowerInvariant();
        if(pairing.Length!=16||pairing.Any(c=>!Uri.IsHexDigit(c))){MessageBox.Show("Ange den 16-teckens hexadecimala parnyckeln som visas i Android Companion.","WI-FI TEST");return;}
        WifiStatusText.Text="Testar Wi-Fi med nonce/HMAC-autentisering...";
        try
        {
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var client=new WifiCompanionClient(host,port,pairing);
            string reply=await client.HelloAsync(cts.Token);
            if(reply=="ERROR unauthorized")throw new IOException("Fel parnyckel.");
            if(!reply.StartsWith("MOBILE_MTP_BACKUP_COMPANION/",StringComparison.Ordinal))throw new IOException("Fel tjänst svarade på porten.");
            WifiStatusText.Text=$"Wi-Fi OK: {host}:{port} – Companion {reply.Split('/').Last()}, nonce/HMAC godkänd med 64-bitars parnyckel. Filinnehåll använder AES-256-GCM.";
            Log($"WI-FI HANDSHAKE OK: {reply} från {host}:{port}; 64-bitars parnyckel verifierad utan klartext. Mediaöverföring använder AES-256-GCM per block i v5.25.");
        }
        catch(Exception ex){WifiStatusText.Text="Ingen kompatibel Wi-Fi Companion svarar eller parnyckeln är fel.";Log("WI-FI TEST: "+ex.GetBaseException().Message);MessageBox.Show("Kunde inte verifiera Mobile MTP Backup Companion.\n\n"+ex.GetBaseException().Message,"WI-FI TEST",MessageBoxButton.OK,MessageBoxImage.Information);}
    }

    private bool IsWifiMode=>ConnectionModeComboBox.SelectedIndex==1;
}
