# Mobile MTP Backup Wi-Fi protocol 0.2

Default TCP port: 8765. One command per connection.

- `HELLO` -> `MOBILE_MTP_BACKUP_COMPANION/0.2`
- `LIST` -> tab-separated media rows: `id name size modified mime`, followed by `END`
- `GET <id>` -> `DATA <size>` then exactly `<size>` raw bytes
- `HASH <id>` -> lowercase SHA-256 hex

The Android companion exposes media through Android MediaStore and never deletes or modifies source files.

Planned desktop transfer flow:
1. HELLO handshake.
2. LIST remote media.
3. Compare against local manifest.
4. GET new/changed files into `.partial` files.
5. Validate byte count and compare local SHA-256 with HASH before final rename.

Security note: protocol 0.2 is LAN-development only. Pairing/authentication and encrypted transport must be added before unattended or untrusted-network use.
