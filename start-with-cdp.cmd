@echo off
rem ============================================================
rem  Start YesPlayMusic with a DevTools port so this plugin can read
rem  the real playback position (SMTC gives metadata only for Electron).
rem
rem  1. Edit the path below if your YesPlayMusic lives elsewhere.
rem  2. Port 9333 is reserved for this plugin; 9222 is often taken by
rem     other tools. The plugin probes 9333 then 9222.
rem  3. The port listens on 127.0.0.1 only.
rem ============================================================

start "" "C:\Program Files\YesPlayMusic\YesPlayMusic-0.4.11.exe" --remote-debugging-port=9333
