using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace MobileMTPBackup;

public partial class MainWindow : Window
{
    private readonly MtpService _mtp=new(); private readonly BackupEngine _backup; private string _currentPath="\\";
    public MainWindow(){InitializeComponent();_backup=new BackupEngine(_mtp);RefreshDevices();}
    private void Log(string msg){LogTextBox.AppendText($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");LogTextBox.ScrollToEnd();}
    private MtpDeviceInfo? SelectedDevice=>DeviceComboBox.SelectedItem as MtpDeviceInfo;
    private EntryRow? SelectedEntry=>EntriesList.SelectedItem as EntryRow;
    private void RefreshDevices(){try{var devices=_mtp.GetDevices();DeviceComboBox.ItemsSource=devices;if(devices.Count>0)DeviceComboBox.SelectedIndex=0;Log(devices.Count>0?$"Hittade {devices.Count} MTP-enhet(er).":"Ingen MTP-enhet hittades. Lås upp mobilen och välj Filöverföring/MTP.");}catch(Exception ex){Log("MTP-fel: "+ex.GetBaseException().Message);}}
    private void RefreshDevices_Click(object sender,RoutedEventArgs e)=>RefreshDevices();
    private void LoadRoot_Click(object sender,RoutedEventArgs e){_currentPath="\\";LoadPath();}
    private void LoadPath(){var d=SelectedDevice;if(d is null){Log("Välj en telefon först.");return;}try{var entries=_currentPath=="\\"?_mtp.GetRootEntries(d):_mtp.GetEntries(d,_currentPath);EntriesList.ItemsSource=entries.Select(x=>new EntryRow(x)).ToList();CurrentPathText.Text=_currentPath;Log($"Läste {entries.Count} objekt från {_currentPath}.");}catch(Exception ex){Log("Kunde inte läsa mappen: "+ex.GetBaseException().Message);}}
    private void EntriesList_MouseDoubleClick(object sender,MouseButtonEventArgs e){if(SelectedEntry?.Entry.IsDirectory==true){_currentPath=SelectedEntry.Entry.FullName;LoadPath();}}
    private void Up_Click(object sender,RoutedEventArgs e){if(_currentPath=="\\")return;string t=_currentPath.TrimEnd('\\');int last=t.LastIndexOf('\\');_currentPath=last<=0?"\\":t[..last];LoadPath();}
    private void ChooseFolder_Click(object sender,RoutedEventArgs e){var dialog=new OpenFolderDialog{Title="Välj backupmapp"};if(dialog.ShowDialog()==true)DestinationTextBox.Text=dialog.FolderName;}
    private async void BackupSelected_Click(object sender,RoutedEventArgs e){var d=SelectedDevice;var row=SelectedEntry;if(d is null){Log("Ingen telefon vald.");return;}if(row is null||row.Entry.IsDirectory){Log("Markera en fil.");return;}try{string basePath=DestinationTextBox.Text.Trim();if(string.IsNullOrWhiteSpace(basePath))throw new InvalidOperationException("Välj backupmapp först.");string root=Path.Combine(basePath,Sanitize(d.Name));Directory.CreateDirectory(root);var rec=await _backup.BackupOneFileAsync(d,row.Entry,root,PreserveDatesCheckBox.IsChecked==true,VerifyCheckBox.IsChecked==true,Log);await BackupEngine.AppendManifestAsync(root,rec);BackupStatusText.Text="KLAR: vald fil säkerhetskopierad.";Log($"SHA-256: {rec.Sha256}");}catch(Exception ex){BackupStatusText.Text="Backup misslyckades.";Log("BACKUP FEL: "+ex.GetBaseException().Message);}}
    private void SystemTest_Click(object sender,RoutedEventArgs e){RefreshDevices();var d=SelectedDevice;if(d is null){MessageBox.Show("Ingen MTP-telefon hittades.","SYSTEMTEST");return;}try{var entries=_mtp.GetRootEntries(d);MessageBox.Show($"MTP OK: {d.Name}\nRoot innehåller {entries.Count} objekt.","SYSTEMTEST");}catch(Exception ex){MessageBox.Show(ex.GetBaseException().Message,"SYSTEMTEST FEL");}}
    private void BackupFolder_Click(object sender,RoutedEventArgs e)=>MessageBox.Show("Hel-mapp backup aktiveras efter första EXE-testet.");
    private void AutoBackup_Click(object sender,RoutedEventArgs e)=>MessageBox.Show("Auto-backup aktiveras efter första EXE-testet.");
    private void Preview_Click(object sender,RoutedEventArgs e)=>MessageBox.Show("Förhandsgranskning aktiveras efter första EXE-testet.");
    private void Pause_Click(object sender,RoutedEventArgs e){} private void Resume_Click(object sender,RoutedEventArgs e){} private void Cancel_Click(object sender,RoutedEventArgs e){}
    private static string Sanitize(string value){foreach(char c in Path.GetInvalidFileNameChars())value=value.Replace(c,'_');return value;}
}
public sealed class EntryRow{public MtpEntry Entry{get;} public string TypeText=>Entry.IsDirectory?"Mapp":"Fil";public string Name=>Entry.Name;public string SizeText=>Entry.IsDirectory?"":Entry.Length?.ToString()??"?";public string DateText=>(Entry.DateCreated??Entry.DateModified)?.ToString("yyyy-MM-dd HH:mm:ss")??"okänt";public EntryRow(MtpEntry entry)=>Entry=entry;}
