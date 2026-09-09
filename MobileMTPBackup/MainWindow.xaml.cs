using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace MobileMTPBackup;

public partial class MainWindow : Window
{
    private readonly MtpService _mtp=new();
    private readonly BackupEngine _backup;
    private string _currentPath="\\";
    private CancellationTokenSource? _folderCts;
    private volatile bool _isPaused;

    public MainWindow(){InitializeComponent();_backup=new BackupEngine(_mtp);RefreshDevices();}
    private void Log(string msg){LogTextBox.AppendText($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");LogTextBox.ScrollToEnd();}
    private MtpDeviceInfo? SelectedDevice=>DeviceComboBox.SelectedItem as MtpDeviceInfo;
    private EntryRow? SelectedEntry=>EntriesList.SelectedItem as EntryRow;
    private void RefreshDevices(){try{Log("Söker efter Windows MTP/WPD-enheter...");var devices=_mtp.GetDevices();DeviceComboBox.ItemsSource=devices;if(devices.Count>0){DeviceComboBox.SelectedIndex=0;Log($"Hittade {devices.Count} MTP-enhet(er): "+string.Join(", ",devices.Select(d=>d.Name)));}else{Log("Ingen MTP-enhet hittades. Windows ska visa mobilen som MTP USB Device och mobilen måste vara upplåst i Filöverföring/MTP-läge.");}Log(_mtp.GetDiagnosticSummary());}catch(Exception ex){Log("MTP DETEKTERINGSFEL: "+ex);}}
    private void RefreshDevices_Click(object sender,RoutedEventArgs e)=>RefreshDevices();
    private void LoadRoot_Click(object sender,RoutedEventArgs e){_currentPath="\\";LoadPath();}
    private void LoadPath(){var d=SelectedDevice;if(d is null){Log("Välj en telefon först.");return;}try{var entries=_currentPath=="\\"?_mtp.GetRootEntries(d):_mtp.GetEntries(d,_currentPath);EntriesList.ItemsSource=entries.Select(x=>new EntryRow(x)).ToList();CurrentPathText.Text=_currentPath;Log($"Läste {entries.Count} objekt från {_currentPath}.");}catch(Exception ex){Log("Kunde inte läsa mappen: "+ex);}}
    private void EntriesList_MouseDoubleClick(object sender,MouseButtonEventArgs e){if(SelectedEntry?.Entry.IsDirectory==true){_currentPath=SelectedEntry.Entry.FullName;LoadPath();}}
    private void Up_Click(object sender,RoutedEventArgs e){if(_currentPath=="\\")return;string t=_currentPath.TrimEnd('\\');int last=t.LastIndexOf('\\');_currentPath=last<=0?"\\":t[..last];LoadPath();}
    private void ChooseFolder_Click(object sender,RoutedEventArgs e){var dialog=new OpenFolderDialog{Title="Välj backupmapp"};if(dialog.ShowDialog()==true)DestinationTextBox.Text=dialog.FolderName;}

    private async void BackupSelected_Click(object sender,RoutedEventArgs e){var d=SelectedDevice;var row=SelectedEntry;if(d is null){Log("Ingen telefon vald.");return;}if(row is null||row.Entry.IsDirectory){Log("Markera en fil.");return;}try{string basePath=DestinationTextBox.Text.Trim();if(string.IsNullOrWhiteSpace(basePath))throw new InvalidOperationException("Välj backupmapp först.");string root=Path.Combine(basePath,Sanitize(d.Name));Directory.CreateDirectory(root);Log($"Backup startar: {row.Entry.FullName} -> {root}");var rec=await _backup.BackupOneFileAsync(d,row.Entry,root,PreserveDatesCheckBox.IsChecked==true,VerifyCheckBox.IsChecked==true,Log);try{await BackupEngine.AppendManifestAsync(root,rec);}catch(Exception mex){Log("MANIFEST VARNING: filen är sparad men manifestet kunde inte uppdateras. "+mex);}BackupStatusText.Text="KLAR: vald fil säkerhetskopierad.";Log($"SHA-256: {rec.Sha256}");}catch(Exception ex){BackupStatusText.Text="Backup misslyckades – se loggen.";var detail=ex.ToString();Log("BACKUP FEL: "+detail);MessageBox.Show(detail,"BACKUP FEL",MessageBoxButton.OK,MessageBoxImage.Error);}}

    private async void BackupFolder_Click(object sender,RoutedEventArgs e)
    {
        var d=SelectedDevice;if(d is null){Log("Ingen telefon vald.");return;}
        if(_folderCts is not null){Log("En hel-mapp backup kör redan.");return;}
        string remoteFolder=SelectedEntry?.Entry.IsDirectory==true?SelectedEntry.Entry.FullName:_currentPath;
        _folderCts=new CancellationTokenSource();_isPaused=false;
        try
        {
            string basePath=DestinationTextBox.Text.Trim();if(string.IsNullOrWhiteSpace(basePath))throw new InvalidOperationException("Välj backupmapp först.");
            string folderName=remoteFolder=="\\"?"Telefonrot":Path.GetFileName(remoteFolder.TrimEnd('\\'));
            string root=Path.Combine(basePath,Sanitize(d.Name),Sanitize(folderName));Directory.CreateDirectory(root);
            BackupFolderButton.IsEnabled=false;PauseButton.IsEnabled=true;ResumeButton.IsEnabled=false;CancelButton.IsEnabled=true;BackupProgressBar.Value=0;BackupStatusText.Text="Hel-mapp backup startar...";
            Log($"HEL-MAPP BACKUP: {remoteFolder} -> {root}");
            var result=await _backup.BackupFolderRecursiveAsync(d,remoteFolder,root,PreserveDatesCheckBox.IsChecked==true,VerifyCheckBox.IsChecked==true,IncrementalCheckBox.IsChecked==true,Log,(done,total,path)=>Dispatcher.Invoke(()=>{BackupProgressBar.Value=total==0?0:(double)done/total*100;BackupStatusText.Text=$"{done}/{total}: {path}";}),_folderCts.Token,()=>_isPaused);
            BackupProgressBar.Value=100;BackupStatusText.Text=$"KLAR: {result.FilesCopied} kopierade, {result.FilesSkipped} hoppades över, {result.FilesFailed} fel.";
            Log($"HEL-MAPP KLAR: kopierade={result.FilesCopied}, överhoppade={result.FilesSkipped}, fel={result.FilesFailed}, byte={result.BytesCopied}");
        }
        catch(OperationCanceledException){BackupStatusText.Text="AVBRUTEN av användaren.";Log("HEL-MAPP BACKUP AVBRUTEN av användaren.");}
        catch(Exception ex){BackupStatusText.Text="Hel-mapp backup misslyckades – se loggen.";Log("HEL-MAPP FEL: "+ex);MessageBox.Show(ex.ToString(),"HEL-MAPP BACKUP FEL",MessageBoxButton.OK,MessageBoxImage.Error);}
        finally
        {
            _isPaused=false;_folderCts?.Dispose();_folderCts=null;
            BackupFolderButton.IsEnabled=true;PauseButton.IsEnabled=false;ResumeButton.IsEnabled=false;CancelButton.IsEnabled=false;
        }
    }

    private void Pause_Click(object sender,RoutedEventArgs e)
    {
        if(_folderCts is null)return;_isPaused=true;PauseButton.IsEnabled=false;ResumeButton.IsEnabled=true;BackupStatusText.Text="PAUSAD – aktuell fil slutförs först.";Log("PAUS begärd.");
    }
    private void Resume_Click(object sender,RoutedEventArgs e)
    {
        if(_folderCts is null)return;_isPaused=false;PauseButton.IsEnabled=true;ResumeButton.IsEnabled=false;BackupStatusText.Text="Fortsätter backup...";Log("FORTSÄTT begärd.");
    }
    private void Cancel_Click(object sender,RoutedEventArgs e)
    {
        if(_folderCts is null)return;CancelButton.IsEnabled=false;BackupStatusText.Text="Avbryter... aktuell fil kan behöva slutföras först.";Log("AVBRYT begärd.");_folderCts.Cancel();
    }

    private void SystemTest_Click(object sender,RoutedEventArgs e){RefreshDevices();var d=SelectedDevice;if(d is null){MessageBox.Show("Ingen MTP-telefon hittades. Kontrollera att Enhetshanteraren visar MTP USB Device och klicka sedan Sök igen.","SYSTEMTEST");return;}try{var entries=_mtp.GetRootEntries(d);MessageBox.Show($"MTP OK: {d.Name}\nRoot innehåller {entries.Count} objekt.\n{_mtp.GetDiagnosticSummary()}","SYSTEMTEST");}catch(Exception ex){MessageBox.Show(ex.ToString(),"SYSTEMTEST FEL");}}
    private void AutoBackup_Click(object sender,RoutedEventArgs e)=>MessageBox.Show("Auto-backup media kommer i nästa version.");
    private void Preview_Click(object sender,RoutedEventArgs e)=>MessageBox.Show("Förhandsgranskning kommer i nästa version.");
    private static string Sanitize(string value){foreach(char c in Path.GetInvalidFileNameChars())value=value.Replace(c,'_');return string.IsNullOrWhiteSpace(value)?"unnamed":value;}
}
public sealed class EntryRow{public MtpEntry Entry{get;} public string TypeText=>Entry.IsDirectory?"Mapp":"Fil";public string Name=>Entry.Name;public string SizeText=>Entry.IsDirectory?"":Entry.Length?.ToString()??"?";public string DateText=>(Entry.DateCreated??Entry.DateModified)?.ToString("yyyy-MM-dd HH:mm:ss")??"okänt";public EntryRow(MtpEntry entry)=>Entry=entry;}
