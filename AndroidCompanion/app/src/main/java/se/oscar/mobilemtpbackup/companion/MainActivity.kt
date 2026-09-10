package se.oscar.mobilemtpbackup.companion

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.provider.MediaStore
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.ServerSocket
import java.security.MessageDigest
import kotlin.concurrent.thread

class MainActivity : AppCompatActivity() {
    private var server: ServerSocket? = null
    private lateinit var status: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        status = TextView(this).apply { textSize=20f; setPadding(32,48,32,32); text="Mobile MTP Backup Companion\n\nFörbereder Wi-Fi-backup..." }
        setContentView(status)
        requestMediaPermissions()
        startServer()
    }

    private fun requestMediaPermissions() {
        val permissions = if (Build.VERSION.SDK_INT >= 33)
            arrayOf(Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VIDEO)
        else arrayOf(Manifest.permission.READ_EXTERNAL_STORAGE)
        val missing=permissions.filter { ActivityCompat.checkSelfPermission(this,it)!=PackageManager.PERMISSION_GRANTED }
        if(missing.isNotEmpty()) ActivityCompat.requestPermissions(this,missing.toTypedArray(),100)
    }

    private fun startServer() = thread(name="wifi-backup-server") {
        try {
            server=ServerSocket(8765)
            runOnUiThread { status.text="Mobile MTP Backup Companion\n\nWi-Fi-tjänst aktiv\nPort: 8765\n\nRedo för trådlös media-backup." }
            while(!Thread.currentThread().isInterrupted) {
                val socket=server!!.accept()
                try {
                    val reader=BufferedReader(InputStreamReader(socket.getInputStream()))
                    val command=reader.readLine()?.trim().orEmpty()
                    val out=socket.getOutputStream()
                    when {
                        command=="HELLO" -> out.write("MOBILE_MTP_BACKUP_COMPANION/0.2\n".toByteArray())
                        command=="LIST" -> {
                            queryMedia().forEach { item -> out.write("${item.id}\t${item.name}\t${item.size}\t${item.modified}\t${item.mime}\n".toByteArray()) }
                            out.write("END\n".toByteArray())
                        }
                        command.startsWith("GET ") -> {
                            val id=command.removePrefix("GET ").toLongOrNull()
                            val item=id?.let { queryMedia().firstOrNull { x->x.id==it } }
                            if(item==null) out.write("ERROR not-found\n".toByteArray()) else {
                                val uri=android.content.ContentUris.withAppendedId(MediaStore.Files.getContentUri("external"),item.id)
                                contentResolver.openInputStream(uri)?.use { input ->
                                    out.write("DATA ${item.size}\n".toByteArray()); input.copyTo(out)
                                } ?: out.write("ERROR open-failed\n".toByteArray())
                            }
                        }
                        command.startsWith("HASH ") -> {
                            val id=command.removePrefix("HASH ").toLongOrNull()
                            val item=id?.let { queryMedia().firstOrNull { x->x.id==it } }
                            if(item==null) out.write("ERROR not-found\n".toByteArray()) else {
                                val uri=android.content.ContentUris.withAppendedId(MediaStore.Files.getContentUri("external"),item.id)
                                val md=MessageDigest.getInstance("SHA-256")
                                contentResolver.openInputStream(uri)?.use { input -> val buf=ByteArray(1024*1024); while(true){val n=input.read(buf);if(n<=0)break;md.update(buf,0,n)} }
                                out.write((md.digest().joinToString(""){"%02x".format(it)}+"\n").toByteArray())
                            }
                        }
                        else -> out.write("ERROR unknown-command\n".toByteArray())
                    }
                    out.flush()
                } finally { socket.close() }
            }
        } catch(ex:Exception) { runOnUiThread { status.text="Wi-Fi-tjänsten stoppades:\n${ex.message}" } }
    }

    private fun queryMedia(): List<MediaItem> {
        val result=mutableListOf<MediaItem>()
        val projection=arrayOf(MediaStore.Files.FileColumns._ID,MediaStore.Files.FileColumns.DISPLAY_NAME,MediaStore.Files.FileColumns.SIZE,MediaStore.Files.FileColumns.DATE_MODIFIED,MediaStore.Files.FileColumns.MIME_TYPE)
        contentResolver.query(MediaStore.Files.getContentUri("external"),projection,null,null,"${MediaStore.Files.FileColumns.DATE_MODIFIED} DESC")?.use { c ->
            val id=c.getColumnIndexOrThrow(MediaStore.Files.FileColumns._ID); val name=c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.DISPLAY_NAME); val size=c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.SIZE); val modified=c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.DATE_MODIFIED); val mime=c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.MIME_TYPE)
            while(c.moveToNext()) { val m=c.getString(mime)?:continue; if(m.startsWith("image/")||m.startsWith("video/")) result+=MediaItem(c.getLong(id),c.getString(name)?:"unnamed",c.getLong(size),c.getLong(modified),m) }
        }
        return result
    }

    override fun onDestroy(){try{server?.close()}catch(_:Exception){};super.onDestroy()}
    data class MediaItem(val id:Long,val name:String,val size:Long,val modified:Long,val mime:String)
}
