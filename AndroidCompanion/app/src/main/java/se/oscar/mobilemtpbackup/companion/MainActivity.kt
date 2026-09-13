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
import java.net.Socket
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
    private val protocolVersion = "MobileMTPBackup-v5.27"
    @Volatile private var lastClientError: String = "ingen"

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        status = TextView(this).apply { textSize = 20f; setPadding(32, 48, 32, 32); text = "Mobile MTP Backup Companion\n\nFörbereder Wi-Fi-backup..." }
        setContentView(status)
        requestMediaPermissions()
        startServer()
    }

    private fun requestMediaPermissions() {
        val permissions = if (Build.VERSION.SDK_INT >= 33) {
            arrayOf(Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VIDEO)
        } else {
            arrayOf(Manifest.permission.READ_EXTERNAL_STORAGE)
        }
        val missing = permissions.filter { ActivityCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED }
        if (missing.isNotEmpty()) ActivityCompat.requestPermissions(this, missing.toTypedArray(), 100)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 100) refreshStatus()
    }

    private fun hasImagePermission(): Boolean = if (Build.VERSION.SDK_INT >= 33) {
        ActivityCompat.checkSelfPermission(this, Manifest.permission.READ_MEDIA_IMAGES) == PackageManager.PERMISSION_GRANTED
    } else {
        ActivityCompat.checkSelfPermission(this, Manifest.permission.READ_EXTERNAL_STORAGE) == PackageManager.PERMISSION_GRANTED
    }

    private fun hasVideoPermission(): Boolean = if (Build.VERSION.SDK_INT >= 33) {
        ActivityCompat.checkSelfPermission(this, Manifest.permission.READ_MEDIA_VIDEO) == PackageManager.PERMISSION_GRANTED
    } else {
        ActivityCompat.checkSelfPermission(this, Manifest.permission.READ_EXTERNAL_STORAGE) == PackageManager.PERMISSION_GRANTED
    }

    private fun hasMediaPermission(): Boolean = hasImagePermission() || hasVideoPermission()

    private fun permissionText(): String = when {
        hasImagePermission() && hasVideoPermission() -> "bilder + video godkända"
        hasImagePermission() -> "endast bilder godkända"
        hasVideoPermission() -> "endast video godkänd"
        else -> "saknas"
    }

    private fun localIpv4(): String? = try {
        NetworkInterface.getNetworkInterfaces().toList()
            .flatMap { it.inetAddresses.toList() }
            .filterIsInstance<Inet4Address>()
            .firstOrNull { !it.isLoopbackAddress && it.isSiteLocalAddress }
            ?.hostAddress
    } catch (_: Exception) { null }

    private fun refreshStatus() = thread(name = "wifi-backup-status") {
        val ip = localIpv4() ?: "ingen lokal IPv4 hittades"
        val permission = permissionText()
        val count = try { if (hasMediaPermission()) queryMedia().size else 0 } catch (_: Exception) { -1 }
        val countText = if (count >= 0) count.toString() else "okänt"
        runOnUiThread {
            status.text = "Mobile MTP Backup Companion 0.12\n\nWi-Fi-tjänst aktiv\nIP-adress: $ip\nPort: 8765\nParnyckel: $pairingCode\nMediaåtkomst: $permission\nBilder/video synliga: $countText\nSenaste klientfel: $lastClientError\n\nAnge IP-adressen och den 16-teckens hexadecimala parnyckeln ovan i Windows-programmet.\nParnyckeln har 64 bitars slumpmässig entropi och gäller bara tills appen stängs.\nAutentisering: unik nonce + HMAC-SHA256.\nSessionsnyckel: HMAC-SHA256-baserad härledning.\nHELLO, LIST, HASH och filinnehåll: AES-256-GCM med autentiserad protokollkontext.\n0.12 isolerar klientfel så en trasig/avbruten anslutning inte ska stoppa servern.\nGET/HASH slår nu upp MediaStore-ID direkt i stället för att skanna hela biblioteket.\nNonce-ramen och fel före godkänd autentisering ligger fortfarande utanför TLS.\nLåt appen vara öppen under testet."
        }
    }

    private fun protocolField(value: String): String = value.replace('\t', ' ').replace('\r', ' ').replace('\n', ' ')

    private fun readCommandLine(input: InputStream, maxBytes: Int = 512): String {
        val bytes = ArrayList<Byte>(64)
        while (bytes.size <= maxBytes) {
            val value = input.read()
            if (value < 0 || value == '\n'.code) break
            if (value != '\r'.code) bytes.add(value.toByte())
        }
        if (bytes.size > maxBytes) throw IllegalArgumentException("command-too-long")
        return bytes.toByteArray().toString(Charsets.UTF_8).trim()
    }

    private fun hex(bytes: ByteArray) = bytes.joinToString("") { "%02x".format(it) }

    private fun hmac(nonce: String, command: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(pairingCode.toByteArray(Charsets.UTF_8), "HmacSHA256"))
        return hex(mac.doFinal((nonce + "\n" + command).toByteArray(Charsets.UTF_8)))
    }

    private fun secureEquals(a: String, b: String): Boolean = try {
        MessageDigest.isEqual(a.lowercase().toByteArray(Charsets.US_ASCII), b.lowercase().toByteArray(Charsets.US_ASCII))
    } catch (_: Exception) { false }

    private fun sessionKey(nonce: String): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(pairingCode.toByteArray(Charsets.UTF_8), "HmacSHA256"))
        return mac.doFinal(("$protocolVersion\nsession\n$nonce").toByteArray(Charsets.UTF_8))
    }

    private fun textAad(context: String, header: String): ByteArray = "$protocolVersion\ntext\n$context\n$header".toByteArray(Charsets.UTF_8)
    private fun mediaAad(context: String, header: String, chunkIndex: Long, plainLength: Int): ByteArray = "$protocolVersion\nmedia\n$context\n$header\n$chunkIndex\n$plainLength".toByteArray(Charsets.UTF_8)

    private fun encryptBytes(plain: ByteArray, key: ByteArray, aad: ByteArray): Pair<ByteArray, ByteArray> {
        val iv = ByteArray(12).also { random.nextBytes(it) }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, iv))
        cipher.updateAAD(aad)
        return iv to cipher.doFinal(plain)
    }

    private fun writeEncryptedText(out: java.io.OutputStream, text: String, key: ByteArray, context: String) {
        val plain = text.toByteArray(Charsets.UTF_8)
        require(plain.size <= 16 * 1024 * 1024) { "encrypted-text-too-large" }
        val header = "ETEXT ${plain.size}"
        val (iv, encrypted) = encryptBytes(plain, key, textAad(context, header))
        out.write((header + "\n").toByteArray())
        out.write(iv)
        out.write(encrypted)
    }

    private fun writeEncryptedMedia(input: InputStream, out: java.io.OutputStream, size: Long, key: ByteArray, context: String) {
        val chunkSize = 1024 * 1024
        val header = "EDATA $size $chunkSize"
        out.write((header + "\n").toByteArray())
        out.flush()
        val buffer = ByteArray(chunkSize)
        var remaining = size
        var chunkIndex = 0L
        while (remaining > 0) {
            val wanted = minOf(chunkSize.toLong(), remaining).toInt()
            var offset = 0
            while (offset < wanted) {
                val n = input.read(buffer, offset, wanted - offset)
                if (n <= 0) throw java.io.EOFException("media-short-read")
                offset += n
            }
            val (iv, encrypted) = encryptBytes(buffer.copyOfRange(0, wanted), key, mediaAad(context, header, chunkIndex, wanted))
            out.write(ByteBuffer.allocate(4).putInt(wanted).array())
            out.write(iv)
            out.write(encrypted)
            remaining -= wanted
            chunkIndex++
        }
    }

    private fun startServer() = thread(name = "wifi-backup-server") {
        try {
            server = ServerSocket().apply { reuseAddress = true; bind(InetSocketAddress(8765)) }
            refreshStatus()
            while (!Thread.currentThread().isInterrupted) {
                val socket = server!!.accept()
                handleClientSafely(socket)
            }
        } catch (ex: Exception) {
            if (!isFinishing && !isDestroyed && server?.isClosed != true) {
                runOnUiThread { status.text = "Wi-Fi-tjänsten stoppades:\n${ex.message}" }
            }
        }
    }

    private fun handleClientSafely(socket: Socket) {
        try {
            handleClient(socket)
            lastClientError = "ingen"
        } catch (ex: Exception) {
            lastClientError = "${ex.javaClass.simpleName}: ${ex.message ?: "okänt fel"}"
            refreshStatus()
        } finally {
            try { socket.close() } catch (_: Exception) { }
        }
    }

    private fun handleClient(socket: Socket) {
        socket.soTimeout = 15_000
        val out = socket.getOutputStream()
        val nonceBytes = ByteArray(16).also { random.nextBytes(it) }
        val nonce = hex(nonceBytes)
        out.write("NONCE $nonce\n".toByteArray())
        out.flush()

        val raw = try {
            readCommandLine(socket.getInputStream())
        } catch (_: IllegalArgumentException) {
            out.write("ERROR command-too-long\n".toByteArray())
            out.flush()
            return
        }

        val parts = raw.split(' ', limit = 3)
        if (parts.size < 3 || parts[0] != "AUTH" || parts[1].length != 64) {
            out.write("ERROR unauthorized\n".toByteArray())
            out.flush()
            return
        }

        val command = parts[2]
        val expected = hmac(nonce, command)
        if (!secureEquals(parts[1], expected)) {
            out.write("ERROR unauthorized\n".toByteArray())
            out.flush()
            return
        }

        val key = sessionKey(nonce)
        if (command != "HELLO" && !hasMediaPermission()) {
            writeEncryptedText(out, "ERROR permission-required\n", key, command)
            out.flush()
            return
        }

        when {
            command == "HELLO" -> writeEncryptedText(out, "MOBILE_MTP_BACKUP_COMPANION/0.12\n", key, command)
            command == "LIST" -> {
                val body = buildString {
                    queryMedia().forEach { item ->
                        append("${item.id}\t${protocolField(item.name)}\t${item.size}\t${item.modified}\t${protocolField(item.mime)}\t${protocolField(item.relativePath)}\n")
                    }
                    append("END\n")
                }
                writeEncryptedText(out, body, key, command)
            }
            command.startsWith("GET ") -> {
                val id = command.removePrefix("GET ").toLongOrNull()
                val item = id?.let { queryMediaById(it) }
                if (item == null) {
                    writeEncryptedText(out, "ERROR not-found\n", key, command)
                } else {
                    val uri = android.content.ContentUris.withAppendedId(MediaStore.Files.getContentUri("external"), item.id)
                    contentResolver.openInputStream(uri)?.use { input ->
                        writeEncryptedMedia(input, out, item.size, key, command)
                    } ?: writeEncryptedText(out, "ERROR open-failed\n", key, command)
                }
            }
            command.startsWith("HASH ") -> {
                val id = command.removePrefix("HASH ").toLongOrNull()
                val item = id?.let { queryMediaById(it) }
                if (item == null) {
                    writeEncryptedText(out, "ERROR not-found\n", key, command)
                } else {
                    val uri = android.content.ContentUris.withAppendedId(MediaStore.Files.getContentUri("external"), item.id)
                    val md = MessageDigest.getInstance("SHA-256")
                    val opened = contentResolver.openInputStream(uri)
                    if (opened == null) {
                        writeEncryptedText(out, "ERROR open-failed\n", key, command)
                    } else {
                        opened.use { input ->
                            val buf = ByteArray(1024 * 1024)
                            while (true) {
                                val n = input.read(buf)
                                if (n <= 0) break
                                md.update(buf, 0, n)
                            }
                        }
                        writeEncryptedText(out, hex(md.digest()) + "\n", key, command)
                    }
                }
            }
            else -> writeEncryptedText(out, "ERROR unknown-command\n", key, command)
        }
        out.flush()
    }

    private fun mediaSelection(id: Long? = null): Pair<String?, Array<String>?> {
        val clauses = mutableListOf<String>()
        val args = mutableListOf<String>()
        if (id != null) {
            clauses += "${MediaStore.Files.FileColumns._ID}=?"
            args += id.toString()
        }
        if (Build.VERSION.SDK_INT >= 33) {
            val mimeClauses = mutableListOf<String>()
            if (hasImagePermission()) mimeClauses += "${MediaStore.Files.FileColumns.MIME_TYPE} LIKE 'image/%'"
            if (hasVideoPermission()) mimeClauses += "${MediaStore.Files.FileColumns.MIME_TYPE} LIKE 'video/%'"
            if (mimeClauses.isEmpty()) return "1=0" to emptyArray()
            clauses += "(" + mimeClauses.joinToString(" OR ") + ")"
        }
        return if (clauses.isEmpty()) null to null else clauses.joinToString(" AND ") to args.toTypedArray()
    }

    private fun mediaColumns(): Array<String> {
        val columns = mutableListOf(
            MediaStore.Files.FileColumns._ID,
            MediaStore.Files.FileColumns.DISPLAY_NAME,
            MediaStore.Files.FileColumns.SIZE,
            MediaStore.Files.FileColumns.DATE_MODIFIED,
            MediaStore.Files.FileColumns.MIME_TYPE
        )
        if (Build.VERSION.SDK_INT >= 29) columns += MediaStore.Files.FileColumns.RELATIVE_PATH
        return columns.toTypedArray()
    }

    private fun cursorItem(c: android.database.Cursor): MediaItem? {
        val id = c.getColumnIndexOrThrow(MediaStore.Files.FileColumns._ID)
        val name = c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.DISPLAY_NAME)
        val size = c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.SIZE)
        val modified = c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.DATE_MODIFIED)
        val mime = c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.MIME_TYPE)
        val relativePath = if (Build.VERSION.SDK_INT >= 29) c.getColumnIndex(MediaStore.Files.FileColumns.RELATIVE_PATH) else -1
        val m = c.getString(mime) ?: return null
        if (!m.startsWith("image/") && !m.startsWith("video/")) return null
        if (m.startsWith("image/") && !hasImagePermission()) return null
        if (m.startsWith("video/") && !hasVideoPermission()) return null
        val rel = if (relativePath >= 0) c.getString(relativePath).orEmpty() else ""
        return MediaItem(c.getLong(id), c.getString(name) ?: "unnamed", c.getLong(size), c.getLong(modified), m, rel)
    }

    private fun queryMedia(): List<MediaItem> {
        val result = mutableListOf<MediaItem>()
        val (selection, args) = mediaSelection()
        contentResolver.query(
            MediaStore.Files.getContentUri("external"),
            mediaColumns(),
            selection,
            args,
            "${MediaStore.Files.FileColumns.DATE_MODIFIED} DESC"
        )?.use { c ->
            while (c.moveToNext()) cursorItem(c)?.let { result += it }
        }
        return result
    }

    private fun queryMediaById(id: Long): MediaItem? {
        val (selection, args) = mediaSelection(id)
        contentResolver.query(
            MediaStore.Files.getContentUri("external"),
            mediaColumns(),
            selection,
            args,
            null
        )?.use { c ->
            if (c.moveToFirst()) return cursorItem(c)
        }
        return null
    }

    override fun onDestroy() {
        try { server?.close() } catch (_: Exception) { }
        super.onDestroy()
    }

    data class MediaItem(val id: Long, val name: String, val size: Long, val modified: Long, val mime: String, val relativePath: String)
}
