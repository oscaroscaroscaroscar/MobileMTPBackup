# Wi-Fi protocol hardening – v5.22

Mobile MTP Backup v5.22 keeps the USB/MTP path unchanged and hardens the development Wi-Fi companion protocol.

Changes:
- Android Companion 0.6 uses a bounded command line (max 256 bytes) instead of an unbounded line read.
- Each accepted socket has a 15-second command-read timeout.
- LIST/GET/HASH require granted media permission; otherwise the companion returns `ERROR permission-required`.
- Pairing remains a six-digit one-time code generated when the companion starts.
- GET still streams read-only media data. The companion never deletes or modifies source media.
- Windows still verifies transferred files with SHA-256 and finalizes from `.partial` only after verification.

Security status: the current LAN protocol is authenticated by the one-time code but is not yet encrypted with TLS. Do not expose port 8765 to the public internet. TLS/certificate pinning is planned as the next security milestone.
