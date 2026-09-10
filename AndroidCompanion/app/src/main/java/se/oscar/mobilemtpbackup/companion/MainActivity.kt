package se.oscar.mobilemtpbackup.companion

import android.os.Bundle
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import java.net.ServerSocket
import kotlin.concurrent.thread

class MainActivity : AppCompatActivity() {
    private var server: ServerSocket? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val status = TextView(this).apply {
            textSize = 20f
            setPadding(32, 48, 32, 32)
            text = "Mobile MTP Backup Companion\n\nStartar Wi-Fi-tjänst på port 8765..."
        }
        setContentView(status)

        thread(name = "wifi-backup-server") {
            try {
                server = ServerSocket(8765)
                runOnUiThread { status.text = "Mobile MTP Backup Companion\n\nWi-Fi-tjänst aktiv\nPort: 8765\n\nDatorn kan nu testa anslutningen." }
                while (!Thread.currentThread().isInterrupted) {
                    val socket = server!!.accept()
                    socket.getOutputStream().bufferedWriter().use {
                        it.write("MOBILE_MTP_BACKUP_COMPANION/0.1\n")
                        it.flush()
                    }
                    socket.close()
                }
            } catch (ex: Exception) {
                runOnUiThread { status.text = "Wi-Fi-tjänsten stoppades:\n${ex.message}" }
            }
        }
    }

    override fun onDestroy() {
        try { server?.close() } catch (_: Exception) { }
        super.onDestroy()
    }
}
