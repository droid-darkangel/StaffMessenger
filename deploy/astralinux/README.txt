StaffMessenger server for Astra Linux x86_64
============================================

Requirements:
- 64-bit Astra Linux with glibc
- PostgreSQL reachable from the server
- systemd

Installation:
1. Extract this archive on the Astra Linux host.
2. Run: sudo ./install.sh
3. Edit: sudo nano /etc/staffmessenger/appsettings.Production.json
4. Replace CHANGE_ME and configure PostgreSQL and other server settings.
5. Run: sudo systemctl start staffmessenger
6. Check: curl http://127.0.0.1:5072/health

Useful commands:
  sudo systemctl status staffmessenger
  sudo journalctl -u staffmessenger -f

The server listens on 0.0.0.0:5072. Put nginx or another reverse proxy with
TLS in front of it before exposing it to the Internet.
