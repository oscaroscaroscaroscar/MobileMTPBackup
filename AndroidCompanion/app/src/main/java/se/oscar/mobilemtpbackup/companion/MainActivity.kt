package se.oscar.mobilemtpbackup.companion

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.provider.MediaStore
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import java.io.InputStream
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
import kotlin.concurrent.thread

class MainActivity : AppCompatActivity() {
    private var server: ServerSocket? = null
    private lateinit var status: TextView
    private val random = SecureRandom()
    private val pairingCode: String = ByteArray(8).also { random.nextBytes(it) }.joinToString("") { "%02x".format(it) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        status = TextView(this).apply { textSize = 20f; setPadding(32, 48, 32, 32); text = "Mobile MTP Backup Companion\n\nFörbereder Wi-Fi-backup..." }
        setContentView(status); requestMediaPermissions(); startServer()
    }

    private fun requestMediaPermissions() {
        val permissions = if (Build.VERSION.SDK_INT >= 33) arrayOf(Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VIDEO) else arrayOf(Manifest.permission.READ_EXTERNAL_STORAGE)
        val missing = permissions.filter { ActivityCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED }
        if (missing.isNotEmpty()) ActivityCompat.requestPermissions(this, missing.toTypedArray(), 100)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) { super.onRequestPermissionsResult(requestCode, permissions, grantResults); if (requestCode == 100) refreshStatus() }

    private fun hasMediaPermission(): Boolean = if (Build.VERSION.SDK_INT >= 33) {
        ActivityCompat.checkSelfPermission(this, Manifest.permission.READ_MEDIA_IMAGES) == PackageManager.PERMISSION_GRANTED || ActivityCompat.checkSelfPermission(this, Manifest.permission.READ_MEDIA_VIDEO) == PackageManager.PERMISSION_GRANTED
    } else ActivityCompat.checkSelfPermission(this, Manifest.permission.READ_EXTERNAL_STORAGE) == PackageManager.PERMISSION_GRANTED

    private fun localIpv4(): String? = try { NetworkInterface.getNetworkInterfaces().toList().flatMap { it.inetAddresses.toList() }.filterIsInstance<Inet4Address>().firstOrNull { !it.isLoopbackAddress && it.isSiteLocalAddress }?.hostAddress } catch (_: Exception) { null }

    private fun refreshStatus() = thread(name = "wifi-backup-status") {
        val ip = localIpv4() ?: "ingen lokal IPv4 hittades"; val permission = if (hasMediaPermission()) "godkänd" else "saknas"
        val count = try { if (hasMediaPermission()) queryMedia().size else 0 } catch (_: Exception) { -1 }; val countText = if (count >= 0) count.toString() else "okänt"
        runOnUiThread { status.text = "Mobile MTP Backup Companion 0.9\n\nWi-Fi-tjänst aktiv\nIP-adress: $ip\nPort: 8765\nParnyckel: $pairingCode\nMediaåtkomst: $permission\nBilder/video synliga: $countText\n\nAnge IP-adressen och den 16-teckens hexadecimala parnyckeln ovan i Windows-programmet.\nParnyckeln har 64 bitars slumpmässig entropi och gäller bara tills appen stängs.\nAutentisering: unik nonce + HMAC-SHA256.\nFilinnehåll: AES-256-GCM-kryptering per block.\nMetadata/listning är fortfarande inte TLS-krypterad.\nLåt appen vara öppen under testet." }
    }

    private fun protocolField(value: String): String = value.replace('\t', ' ').replace('\r', ' ').replace('\n', ' ')
    private fun readCommandLine(input: InputStream, maxBytes: Int = 512): String { val bytes=ArrayList<Byte>(64); while(bytes.size<=maxBytes){val value=input.read();if(value<0||value=='\n'.code)break;if(value!='\r'.code)bytes.add(value.toByte())};if(bytes.size>maxBytes)throw IllegalArgumentException("command-too-long");return bytes.toByteArray().toString(Charsets.UTF_8).trim() }
    private fun hex(bytes: ByteArray)=bytes.joinToString(""){"%02x".format(it)}
    private fun hmac(nonce:String,command:String):String { val mac=Mac.getInstance("HmacSHA256");mac.init(SecretKeySpec(pairingCode.toByteArray(Charsets.UTF_8),"HmacSHA256"));return hex(mac.doFinal((nonce+"\n"+command).toByteArray(Charsets.UTF_8))) }
    private fun secureEquals(a:String,b:String):Boolean = try { MessageDigest.isEqual(a.lowercase().toByteArray(Charsets.US_ASCII),b.lowercase().toByteArray(Charsets.US_ASCII)) } catch (_:Exception){false}
    private fun sessionKey(nonce:String):ByteArray = MessageDigest.getInstance("SHA-256").digest("MobileMTPBackup-v5.24\n$pairingCode\n$nonce".toByteArray(Charsets.UTF_8))

    private fun writeEncryptedMedia(input:InputStream,out:java.io.OutputStream,size:Long,key:ByteArray){
        val chunkSize=1024*1024
        out.write("EDATA $size $chunkSize\n".toByteArray());out.flush()
        val buffer=ByteArray(chunkSize)
        var remaining=size
        while(remaining>0){
            val wanted=minOf(chunkSize.toLong(),remaining).toInt()
            var offset=0
            while(offset<wanted){val n=input.read(buffer,offset,wanted-offset);if(n<=0)throw java.io.EOFException("media-short-read");offset+=n}
            val iv=ByteArray(12).also{random.nextBytes(it)}
            val cipher=Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.ENCRYPT_MODE,SecretKeySpec(key,"AES"),GCMParameterSpec(128,iv))
            val encrypted=cipher.doFinal(buffer,0,wanted)
            out.write(ByteBuffer.allocate(4).putInt(wanted).array())
            out.write(iv)
            out.write(encrypted)
            remaining-=wanted
        }
    }

    private fun startServer() = thread(name = "wifi-backup-server") {
        try {
            server = ServerSocket().apply { reuseAddress=true; bind(InetSocketAddress(8765)) }; refreshStatus()
            while (!Thread.currentThread().isInterrupted) {
                val socket=server!!.accept()
                try {
                    socket.soTimeout=15_000; val out=socket.getOutputStream(); val nonceBytes=ByteArray(16).also{random.nextBytes(it)}; val nonce=hex(nonceBytes); out.write("NONCE $nonce\n".toByteArray());out.flush()
                    val raw=try{readCommandLine(socket.getInputStream())}catch(_:IllegalArgumentException){out.write("ERROR command-too-long\n".toByteArray());out.flush();continue}
                    val parts=raw.split(' ',limit=3); if(parts.size<3||parts[0]!="AUTH"||parts[1].length!=64){out.write("ERROR unauthorized\n".toByteArray());out.flush();continue}
                    val command=parts[2]; val expected=hmac(nonce,command); if(!secureEquals(parts[1],expected)){out.write("ERROR unauthorized\n".toByteArray());out.flush();continue}
                    if(command!="HELLO"&&!hasMediaPermission()){out.write("ERROR permission-required\n".toByteArray());out.flush();continue}
                    when {
                        command=="HELLO" -> out.write("MOBILE_MTP_BACKUP_COMPANION/0.9\n".toByteArray())
                        command=="LIST" -> { queryMedia().forEach{item->out.write("${item.id}\t${protocolField(item.name)}\t${item.size}\t${item.modified}\t${protocolField(item.mime)}\t${protocolField(item.relativePath)}\n".toByteArray())};out.write("END\n".toByteArray()) }
                        command.startsWith("GET ") -> { val id=command.removePrefix("GET ").toLongOrNull();val item=id?.let{queryMedia().firstOrNull{x->x.id==it}};if(item==null)out.write("ERROR not-found\n".toByteArray())else{val uri=android.content.ContentUris.withAppendedId(MediaStore.Files.getContentUri("external"),item.id);contentResolver.openInputStream(uri)?.use{input->writeEncryptedMedia(input,out,item.size,sessionKey(nonce))}?:out.write("ERROR open-failed\n".toByteArray())} }
                        command.startsWith("HASH ") -> { val id=command.removePrefix("HASH ").toLongOrNull();val item=id?.let{queryMedia().firstOrNull{x->x.id==it}};if(item==null)out.write("ERROR not-found\n".toByteArray())else{val uri=android.content.ContentUris.withAppendedId(MediaStore.Files.getContentUri("external"),item.id);val md=MessageDigest.getInstance("SHA-256");contentResolver.openInputStream(uri)?.use{input->val buf=ByteArray(1024*1024);while(true){val n=input.read(buf);if(n<=0)break;md.update(buf,0,n)}};out.write((hex(md.digest())+"\n").toByteArray())} }
                        else -> out.write("ERROR unknown-command\n".toByteArray())
                    };out.flush()
                } finally { socket.close() }
            }
        } catch(ex:Exception){ if(!isFinishing&&!isDestroyed)runOnUiThread{status.text="Wi-Fi-tjänsten stoppades:\n${ex.message}"} }
    }

    private fun queryMedia(): List<MediaItem> {
        val result=mutableListOf<MediaItem>();val columns=mutableListOf(MediaStore.Files.FileColumns._ID,MediaStore.Files.FileColumns.DISPLAY_NAME,MediaStore.Files.FileColumns.SIZE,MediaStore.Files.FileColumns.DATE_MODIFIED,MediaStore.Files.FileColumns.MIME_TYPE);if(Build.VERSION.SDK_INT>=29)columns+=MediaStore.Files.FileColumns.RELATIVE_PATH
        contentResolver.query(MediaStore.Files.getContentUri("external"),columns.toTypedArray(),null,null,"${MediaStore.Files.FileColumns.DATE_MODIFIED} DESC")?.use{c->val id=c.getColumnIndexOrThrow(MediaStore.Files.FileColumns._ID);val name=c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.DISPLAY_NAME);val size=c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.SIZE);val modified=c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.DATE_MODIFIED);val mime=c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.MIME_TYPE);val relativePath=if(Build.VERSION.SDK_INT>=29)c.getColumnIndex(MediaStore.Files.FileColumns.RELATIVE_PATH)else-1;while(c.moveToNext()){val m=c.getString(mime)?:continue;if(m.startsWith("image/")||m.startsWith("video/")){val rel=if(relativePath>=0)c.getString(relativePath).orEmpty()else"";result+=MediaItem(c.getLong(id),c.getString(name)?:"unnamed",c.getLong(size),c.getLong(modified),m,rel)}}}
        return result
    }

    override fun onDestroy(){try{server?.close()}catch(_:Exception){};super.onDestroy()}
    data class MediaItem(val id:Long,val name:String,val size:Long,val modified:Long,val mime:String,val relativePath:String)
}
