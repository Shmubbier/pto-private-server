@echo off
rem Opens the Windows Firewall for the PTO server (TCP 51338). RIGHT-CLICK this file ->
rem "Run as administrator". You only need to do this once per host machine.
netsh advfirewall firewall add rule name="PTO Server 51338" dir=in action=allow protocol=TCP localport=51338
echo.
if %errorlevel%==0 (echo Firewall opened for TCP 51338.) else (echo FAILED - did you run as administrator?)
echo.
pause
