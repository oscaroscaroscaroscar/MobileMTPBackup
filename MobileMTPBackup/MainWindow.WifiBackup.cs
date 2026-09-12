using System.IO;
using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private CancellationTokenSource? _wifiCts;

    private static string SafeFileName(string value)
    {
        var invalid=Path.GetInvalidFileNameChars();
        string safe=string.Concat(value.Select(ch=>invalid.Contains(ch)?'_':ch)).Trim();
        return string.IsNullOrWhiteSpace(safe)?"unnamed":safe;
    }

    private static string SafeRelativeMediaPath(string relativePath)
    {
        if(string.IsNullOrWhiteSpace(relativePath))return "Media";
        var parts=relativePath.Replace('\\','/').Split('/',StringSplitOptions.RemoveEmptyEntries)
            .Where(p=>p!="."&&p!="..")
            .Select(SafeFileName)
            .Where(p=>!string.IsNullOrWhiteSpace(p))
            .Take(12)
            .ToArray();
        return parts.Length==0?"Media":Path.Combine(parts);
    }

    private async void WifiBackupMedia_Click(object sender,RoutedEventArgs e)
    {
        if(!IsWifiMode){MessageBox.Show("Välj Wi-Fi som anslutningsläge först.","WI-FI BACKUP");return;}
        if(_wifiCts is not null){MessageBox.Show("En Wi-Fi-backup kör redan.","WI-FI BACKUP");return;}
        string host=WifiHostTextBox.Text.Trim();
        if(string.IsNullOrWhiteSpace(host)||!int.TryParse(WifiPortTextBox.Text.Trim(),out int port)||port is <1 or >65535){MessageBox.Show("Kontrollera IP-adress och port.","WI-FI BACKUP");return;}
        string pairing=WifiPairCodeTextBox.Text.Trim().ToLowerInvariant();if(pairing.Length!=16||pairing.Any(c=>!Uri.IsHexDigit(c))){MessageBox.Show("Ange den 16-teckens hexadecimala parnyckeln från Android Companion.","WI-FI BACKUP");return;}
        string root=DestinationTextBox.Text.Trim();if(string.IsNullOrWhiteSpace(root)){MessageBox.Show("Välj backupmapp.","WI-FI BACKUP");return;}

        DateTime startedAt=DateTime.Now;string? reportRoot=null;int copied=0,skipped=0,failed=0;long bytesCopied=0;
        _wifiCts=new CancellationTokenSource();var token=_wifiCts.Token;var client=new WifiCompanionClient(host,port,pairing);
        try
        {
            WifiBackupButton.IsEnabled=false;WifiCancelButton.IsEnabled=true;BackupProgressBar.Value=0;
            WifiStatusText.Text="Läser medialistan från telefonen...";
            var items=await client.ListAsync(token);token.ThrowIfCancellationRequested();

            string library=Path.Combine(root,"WiFi");Directory.CreateDirectory(library);reportRoot=library;
            bool incremental=IncrementalCheckBox.IsChecked==true;bool verify=VerifyCheckBox.IsChecked==true;
            var work=new List<(WifiMediaItem Item,string Target,bool Skip)>();long requiredBytes=0;int checkedCount=0;
            foreach(var item in items)
            {
                token.ThrowIfCancellationRequested();
                string folder=Path.Combine(library,SafeRelativeMediaPath(item.RelativePath));
                string safe=SafeFileName(item.Name);
                string target=Path.Combine(folder,$"{item.Id}_{safe}");bool skip=false;
                if(incremental&&File.Exists(target))
                {
                    WifiStatusText.Text=$"Kontrollerar befintlig fil {checkedCount+1}/{items.Count}: {item.Name}";
                    skip=await client.CanSkipExistingAsync(item,target,verify,token);
                    if(!skip)target=Path.Combine(folder,$"{item.Id}_{DateTime.Now:yyyyMMdd-HHmmssfff}_{safe}");
                }
                if(skip)skipped++;else requiredBytes+=Math.Max(0,item.Size);
                work.Add((item,target,skip));checkedCount++;
            }
            EnsureFreeSpace(root,requiredBytes);
            Log($"WI-FI FÖRKONTROLL: {items.Count} totalt, {skipped} kan hoppas över, cirka {FormatBytes(requiredBytes)} behöver kopieras.");
            Log($"WI-FI BACKUP START: {host}:{port} -> {library}; Android-mappar bevaras när MediaStore rapporterar dem.");

            int done=0;
            foreach(var entry in work)
            {
                token.ThrowIfCancellationRequested();var item=entry.Item;
                if(entry.Skip)
                {
                    Log($"WI-FI HOPPAR ÖVER: {item.Name} – befintlig fil matchar.");
                }
                else
                {
                    try
                    {
                        WifiStatusText.Text=$"Wi-Fi {done+1}/{items.Count}: {item.Name}";
                        await client.DownloadVerifiedAsync(item,entry.Target,token);copied++;bytesCopied+=Math.Max(0,item.Size);
                        Log($"WI-FI OK: {item.Name} ({item.Size:N0} bytes) -> {entry.Target}");
                    }
                    catch(OperationCanceledException){throw;}
                    catch(Exception ex){failed++;Log($"WI-FI FEL: {item.Name}: {ex.GetBaseException().Message}");}
                }
                done++;BackupProgressBar.Value=items.Count==0?100:(double)done/items.Count*100;
            }
            BackupProgressBar.Value=100;WifiStatusText.Text=$"Wi-Fi backup klar: {copied} kopierade, {skipped} hoppades över, {failed} fel.";
            try{string report=await SessionReportWriter.WriteAsync(library,new BackupSessionReport(AppVersion,$"Wi-Fi {host}:{port}","Android MediaStore",library,startedAt,DateTime.Now,copied,skipped,failed,bytesCopied,false,null));Log("WI-FI SESSIONSRAPPORT: "+report);}catch(Exception rex){Log("WI-FI RAPPORT VARNING: "+rex.GetBaseException().Message);}
            MessageBox.Show($"Wi-Fi backup klar.\n\nKopierade: {copied}\nHoppades över: {skipped}\nFel: {failed}\nData: {FormatBytes(bytesCopied)}\nMapp: {library}","WI-FI BACKUP");
        }
        catch(OperationCanceledException)
        {
            WifiStatusText.Text=$"Wi-Fi backup avbruten: {copied} kopierade, {skipped} hoppades över.";Log("WI-FI BACKUP AVBRUTEN av användaren.");
            if(reportRoot is not null)try{string report=await SessionReportWriter.WriteAsync(reportRoot,new BackupSessionReport(AppVersion,$"Wi-Fi {host}:{port}","Android MediaStore",reportRoot,startedAt,DateTime.Now,copied,skipped,failed,bytesCopied,true,null));Log("WI-FI SESSIONSRAPPORT: "+report);}catch(Exception rex){Log("WI-FI RAPPORT VARNING: "+rex.GetBaseException().Message);}
        }
        catch(Exception ex)
        {
            WifiStatusText.Text="Wi-Fi backup misslyckades.";Log("WI-FI BACKUP FEL: "+ex);
            if(reportRoot is not null)try{string report=await SessionReportWriter.WriteAsync(reportRoot,new BackupSessionReport(AppVersion,$"Wi-Fi {host}:{port}","Android MediaStore",reportRoot,startedAt,DateTime.Now,copied,skipped,Math.Max(1,failed),bytesCopied,false,ex.GetBaseException().Message));Log("WI-FI SESSIONSRAPPORT: "+report);}catch(Exception rex){Log("WI-FI RAPPORT VARNING: "+rex.GetBaseException().Message);}
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
