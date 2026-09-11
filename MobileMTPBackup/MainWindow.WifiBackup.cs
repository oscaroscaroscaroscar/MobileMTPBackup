using System.IO;
using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private CancellationTokenSource? _wifiCts;

    private async void WifiBackupMedia_Click(object sender,RoutedEventArgs e)
    {
        if(!IsWifiMode){MessageBox.Show("Välj Wi-Fi som anslutningsläge först.","WI-FI BACKUP");return;}
        if(_wifiCts is not null){MessageBox.Show("En Wi-Fi-backup kör redan.","WI-FI BACKUP");return;}
        string host=WifiHostTextBox.Text.Trim();
        if(string.IsNullOrWhiteSpace(host)||!int.TryParse(WifiPortTextBox.Text.Trim(),out int port)||port is <1 or >65535){MessageBox.Show("Kontrollera IP-adress och port.","WI-FI BACKUP");return;}
        string pairing=WifiPairCodeTextBox.Text.Trim();if(pairing.Length!=6||pairing.Any(c=>!char.IsDigit(c))){MessageBox.Show("Ange den 6-siffriga parkoden från Android Companion.","WI-FI BACKUP");return;}
        string root=DestinationTextBox.Text.Trim();if(string.IsNullOrWhiteSpace(root)){MessageBox.Show("Välj backupmapp.","WI-FI BACKUP");return;}

        DateTime startedAt=DateTime.Now;string? reportRoot=null;int copied=0,failed=0;long bytesCopied=0;
        _wifiCts=new CancellationTokenSource();var token=_wifiCts.Token;var client=new WifiCompanionClient(host,port,pairing);
        try
        {
            WifiBackupButton.IsEnabled=false;WifiCancelButton.IsEnabled=true;BackupProgressBar.Value=0;
            WifiStatusText.Text="Läser medialistan från telefonen...";
            var items=await client.ListAsync(token);token.ThrowIfCancellationRequested();
            long totalBytes=items.Where(x=>x.Size>0).Sum(x=>x.Size);EnsureFreeSpace(root,totalBytes);
            string session=Path.Combine(root,"WiFi",DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(session);reportRoot=session;
            Log($"WI-FI BACKUP START: {items.Count} filer, cirka {FormatBytes(totalBytes)}, {host}:{port} -> {session}");
            int done=0;
            foreach(var item in items)
            {
                token.ThrowIfCancellationRequested();
                string safe=string.Concat(item.Name.Select(ch=>Path.GetInvalidFileNameChars().Contains(ch)?'_':ch));
                string target=Path.Combine(session,$"{item.Id}_{safe}");
                try
                {
                    WifiStatusText.Text=$"Wi-Fi {done+1}/{items.Count}: {item.Name}";
                    await client.DownloadVerifiedAsync(item,target,token);copied++;bytesCopied+=Math.Max(0,item.Size);
                    Log($"WI-FI OK: {item.Name} ({item.Size:N0} bytes)");
                }
                catch(OperationCanceledException){throw;}
                catch(Exception ex){failed++;Log($"WI-FI FEL: {item.Name}: {ex.GetBaseException().Message}");}
                done++;BackupProgressBar.Value=items.Count==0?100:(double)done/items.Count*100;
            }
            BackupProgressBar.Value=100;WifiStatusText.Text=$"Wi-Fi backup klar: {copied} kopierade, {failed} fel.";
            try{string report=await SessionReportWriter.WriteAsync(session,new BackupSessionReport(AppVersion,$"Wi-Fi {host}:{port}","Android MediaStore",session,startedAt,DateTime.Now,copied,0,failed,bytesCopied,false,null));Log("WI-FI SESSIONSRAPPORT: "+report);}catch(Exception rex){Log("WI-FI RAPPORT VARNING: "+rex.GetBaseException().Message);}
            MessageBox.Show($"Wi-Fi backup klar.\n\nKopierade: {copied}\nFel: {failed}\nData: {FormatBytes(bytesCopied)}\nMapp: {session}","WI-FI BACKUP");
        }
        catch(OperationCanceledException)
        {
            WifiStatusText.Text=$"Wi-Fi backup avbruten: {copied} filer hann kopieras.";Log("WI-FI BACKUP AVBRUTEN av användaren.");
            if(reportRoot is not null)try{string report=await SessionReportWriter.WriteAsync(reportRoot,new BackupSessionReport(AppVersion,$"Wi-Fi {host}:{port}","Android MediaStore",reportRoot,startedAt,DateTime.Now,copied,0,failed,bytesCopied,true,null));Log("WI-FI SESSIONSRAPPORT: "+report);}catch(Exception rex){Log("WI-FI RAPPORT VARNING: "+rex.GetBaseException().Message);}
        }
        catch(Exception ex)
        {
            WifiStatusText.Text="Wi-Fi backup misslyckades.";Log("WI-FI BACKUP FEL: "+ex);
            if(reportRoot is not null)try{string report=await SessionReportWriter.WriteAsync(reportRoot,new BackupSessionReport(AppVersion,$"Wi-Fi {host}:{port}","Android MediaStore",reportRoot,startedAt,DateTime.Now,copied,0,Math.Max(1,failed),bytesCopied,false,ex.GetBaseException().Message));Log("WI-FI SESSIONSRAPPORT: "+report);}catch(Exception rex){Log("WI-FI RAPPORT VARNING: "+rex.GetBaseException().Message);}
            MessageBox.Show(ex.GetBaseException().Message,"WI-FI BACKUP");
        }
        finally
        {
            _wifiCts?.Dispose();_wifiCts=null;WifiBackupButton.IsEnabled=true;WifiCancelButton.IsEnabled=false;
        }
    }

    private void WifiCancel_Click(object sender,RoutedEventArgs e)
    {
        if(_wifiCts is null)return;WifiCancelButton.IsEnabled=false;WifiStatusText.Text="Avbryter Wi-Fi-backup...";Log("WI-FI AVBRYT begärd.");_wifiCts.Cancel();
    }
}
