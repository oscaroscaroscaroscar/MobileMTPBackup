using System.IO;
using System.Net.Sockets;
using System.Windows;

namespace MobileMTPBackup;

public partial class MainWindow
{
    private const string RecommendedCompanionVersion = "0.12";

    private async void TestWifi_Click(object sender, RoutedEventArgs e)
    {
        string host=WifiHostTextBox.Text.Trim();
        if(string.IsNullOrWhiteSpace(host)){MessageBox.Show("Ange telefonens IP-adress eller värdnamn.","WI-FI TEST");return;}
        if(!int.TryParse(WifiPortTextBox.Text.Trim(),out int port)||port is <1 or >65535){MessageBox.Show("Ange en giltig port mellan 1 och 65535.","WI-FI TEST");return;}
        string pairing=WifiPairCodeTextBox.Text.Trim().ToLowerInvariant();
        if(pairing.Length!=16||pairing.Any(c=>!Uri.IsHexDigit(c))){MessageBox.Show("Ange den 16-teckens hexadecimala parnyckeln som visas i Android Companion.","WI-FI TEST");return;}
        WifiStatusText.Text="Testar Wi-Fi med nonce/HMAC och krypterad HELLO...";
        try
        {
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var client=new WifiCompanionClient(host,port,pairing);
            string reply=await client.HelloAsync(cts.Token);
            if(!reply.StartsWith("MOBILE_MTP_BACKUP_COMPANION/",StringComparison.Ordinal))throw new IOException("Fel tjänst svarade på porten.");
            string companionVersion=reply.Split('/').Last();
            string appVersion=GetCurrentAppVersion();
            WifiStatusText.Text=$"Wi-Fi OK: {host}:{port} – Companion {companionVersion}, nonce/HMAC godkänd. HELLO, LIST, HASH och media använder AES-256-GCM med autentiserad protokollkontext.";
            Log($"WI-FI HANDSHAKE OK: {reply} från {host}:{port}; parnyckeln skickades inte i klartext. Windows {appVersion} sparar en sanerad diagnostikrapport.");
            string recommendation=companionVersion==RecommendedCompanionVersion
                ? "Anslutningen är verifierad. Nästa steg är WI-FI BACKUP MEDIA."
                : $"Anslutningen fungerar, men rekommenderad Companion-version för denna build är {RecommendedCompanionVersion}. Aktuell telefonversion: {companionVersion}.";
            string? report=TryWriteWifiDiagnosticReport(host,port,true,reply,recommendation);
            if(report is not null)Log("WI-FI DIAGNOSTIK SPARAD: "+report);
        }
        catch(Exception ex)
        {
            string root=ex.GetBaseException().Message;
            string help=GetWifiDiagnostic(ex);
            WifiStatusText.Text="Wi-Fi-test misslyckades – se loggen för exakt nästa steg.";
            Log($"WI-FI TEST MISSLYCKADES: {root}\nDIAGNOS: {help}\nMål: {host}:{port}; Companion ska vara öppen och telefon/dator på samma lokala nätverk.");
            string? report=TryWriteWifiDiagnosticReport(host,port,false,root,help);
            if(report is not null)Log("WI-FI DIAGNOSTIK SPARAD: "+report);
            MessageBox.Show($"Kunde inte verifiera Mobile MTP Backup Companion.\n\n{root}\n\nFörslag:\n{help}"+(report is null?"":$"\n\nDiagnostikrapport:\n{report}"),"WI-FI TEST",MessageBoxButton.OK,MessageBoxImage.Information);
        }
    }

    private static string GetCurrentAppVersion()
    {
        Version? version=typeof(MainWindow).Assembly.GetName().Version;
        return version is null ? "unknown" : $"v{version.Major}.{version.Minor}";
    }

    private string? TryWriteWifiDiagnosticReport(string host,int port,bool success,string detail,string recommendation)
    {
        try
        {
            string destination=DestinationTextBox.Text.Trim();
            if(string.IsNullOrWhiteSpace(destination))return null;
            string folder=Path.Combine(destination,"_diagnostics");
            Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,$"wifi-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            string version=typeof(MainWindow).Assembly.GetName().Version?.ToString()??"unknown";
            string text=$"Mobile MTP Backup Wi-Fi diagnostic\n"+
                        $"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n"+
                        $"App version: {version}\n"+
                        $"Recommended Companion: {RecommendedCompanionVersion}\n"+
                        $"OS: {Environment.OSVersion}\n"+
                        $"Target host: {host}\n"+
                        $"Target port: {port}\n"+
                        $"Pairing secret: NOT INCLUDED\n"+
                        $"Result: {(success?"SUCCESS":"FAILURE")}\n"+
                        $"Detail: {detail}\n"+
                        $"Recommendation: {recommendation}\n";
            File.WriteAllText(path,text);
            return path;
        }
        catch(Exception reportEx)
        {
            Log("Kunde inte spara Wi-Fi-diagnostikrapport: "+reportEx.GetBaseException().Message);
            return null;
        }
    }

    private static string GetWifiDiagnostic(Exception ex)
    {
        Exception root=ex.GetBaseException();
        string message=root.Message.ToLowerInvariant();
        if(root is OperationCanceledException || root is TimeoutException)
            return "Anslutningen tog för lång tid. Kontrollera att Companion är öppen, att IP-adressen fortfarande stämmer och att båda enheterna är på samma Wi-Fi. Kontrollera även Windows-brandväggen.";
        if(root is SocketException socket)
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "Telefonen svarade men porten nekade anslutningen. Starta Android Companion och kontrollera att port 8765 visas i appen.",
                SocketError.HostUnreachable or SocketError.NetworkUnreachable => "Datorn kan inte nå telefonens nätverk. Kontrollera att båda enheterna är på samma Wi-Fi och att gästnät/AP-isolering inte används.",
                SocketError.TimedOut => "Nätverksanslutningen fick timeout. Kontrollera IP-adress, samma Wi-Fi och brandvägg/AP-isolering.",
                _ => $"Nätverksfel ({socket.SocketErrorCode}). Kontrollera IP-adress, samma Wi-Fi, Companion och Windows-brandvägg."
            };
        if(message.Contains("unauthorized") || message.Contains("hmac") || message.Contains("authentication") || message.Contains("autent"))
            return "Anslutningen nådde Companion men autentiseringen misslyckades. Skriv in exakt den aktuella 16-teckens parnyckeln som visas i Android Companion; nyckeln ändras när appen startas om.";
        if(message.Contains("decrypt") || message.Contains("gcm") || message.Contains("tag"))
            return $"Krypteringskontrollen misslyckades. Använd aktuell Windows-build tillsammans med Android Companion {RecommendedCompanionVersion} och prova en ny parnyckel genom att starta om Companion.";
        if(message.Contains("fel tjänst") || message.Contains("protocol") || message.Contains("protokoll"))
            return $"Något annat än rätt Companion/protokoll svarade. Kontrollera telefonens IP, port 8765 och att Android Companion {RecommendedCompanionVersion} är öppen.";
        return $"Kontrollera Android Companion {RecommendedCompanionVersion}, telefonens IP-adress, port 8765, den aktuella 16-teckens parnyckeln, samma Wi-Fi och Windows-brandväggen.";
    }

    private bool IsWifiMode=>ConnectionModeComboBox.SelectedIndex==1;
}
