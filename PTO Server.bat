@echo off
REM One-press PTO server launcher (first release). Build fresh, then open the server.
REM No bot: real player-vs-player. Close this window to stop the server.
cd /d "%~dp0"

echo Stopping any running PTO server...
taskkill /f /im PtoServer.exe >nul 2>&1

echo Building...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 (
  echo.
  echo BUILD FAILED - see the error above.
  pause
  exit /b 1
)

echo.
echo ============================================================
echo  PTO SERVER RUNNING on port 51338
echo  Players connect via settings.ini IP:
echo    - same machine: 127.0.0.1
echo    - LAN:          this PC's local IP (ipconfig)
echo  New players click Register in the client to make an account.
echo  Close this window to stop the server.
echo ============================================================
echo.
PtoServer.exe
echo.
echo Server stopped.
pause
