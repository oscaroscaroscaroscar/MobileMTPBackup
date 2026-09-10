using System.IO;
using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private async void WifiBackupMedia_Click(object sender,RoutedEventArgs e)
    {
        if(!IsWifiMode){MessageBox.Show("Välj Wi-Fi som anslutningsläge först.","WI-FI BACKUP");return;}
        string host=WifiHostTextBox.Text.Trim();
        if(string.IsNullOrWhiteSpace(host)||!int.TryParse(WifiPortTextBox.Text.Trim(),out int port)){MessageBox.Show("Kontrollera IP-adress och port.","WI-FI BACKUP");return;}
        string root=DestinationTextBox.Text.Trim(); if(string.IsNullOrWhiteSpace(root)){MessageBox.Show("Välj backupmapp.","WI-FI BACKUP");return;}
        var client=new WifiCompanionClient(host,port); using var cts=new CancellationTokenSource();
        try
        {
            WifiStatusText.Text="Läser medialistan från telefonen...";
            var items=await client.ListAsync(cts.Token); int copied=0,failed=0;
            string session=Path.Combine(root,"WiFi",DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(session);
            foreach(var item in items)
            {
                string safe=string.Concat(item.Name.Select(ch=>Path.GetInvalidFileNameChars().Contains(ch)?'_':ch));
                string target=Path.Combine(session,$"{item.Id}_{safe}");
                try{await client.DownloadVerifiedAsync(item,target,cts.Token);copied++;Log($"WI-FI OK: {item.Name} ({item.Size:N0} bytes)");}
                catch(Exception ex){failed++;Log($"WI-FI FEL: {item.Name}: {ex.GetBaseException().Message}");}
            }
            WifiStatusText.Text=$"Wi-Fi backup klar: {copied} kopierade, {failed} fel.";
            MessageBox.Show($"Wi-Fi backup klar.\n\nKopierade: {copied}\nFel: {failed}\nMapp: {session}","WI-FI BACKUP");
        }
        catch(Exception ex){WifiStatusText.Text="Wi-Fi backup misslyckades.";Log("WI-FI BACKUP FEL: "+ex.GetBaseException().Message);MessageBox.Show(ex.GetBaseException().Message,"WI-FI BACKUP");}
    }
}
