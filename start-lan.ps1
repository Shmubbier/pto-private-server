# LAN launcher: runs the server for HUMAN vs HUMAN across two devices on the same network.
# Bot is OFF (so the two players wait to pair). Decks are shuffled; any username auto-gets a deck.
# Run this on the HOST machine. Right-click -> "Run with PowerShell", or run as Administrator so it
# can open the firewall port automatically.
Set-Location $PSScriptRoot

Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if (-not (Test-Path ".\PtoServer.exe")) {
    Write-Host "Building server..."
    & "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /out:PtoServer.exe PtoServer.cs | Out-Null
}

# --- Firewall: allow inbound TCP 51338 (needs admin) ---
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not (Get-NetFirewallRule -DisplayName "PTO Server 51338" -ErrorAction SilentlyContinue)) {
    if ($admin) {
        New-NetFirewallRule -DisplayName "PTO Server 51338" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 51338 -Profile Any | Out-Null
        Write-Host "Firewall rule added for TCP 51338." -ForegroundColor Green
    } else {
        Write-Host "NOT running as Administrator - could not add the firewall rule." -ForegroundColor Yellow
        Write-Host "Open an ADMIN PowerShell once and run:" -ForegroundColor Yellow
        Write-Host '  New-NetFirewallRule -DisplayName "PTO Server 51338" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 51338 -Profile Any'
        Write-Host "(Or click 'Allow access' if Windows pops up a firewall prompt when the server starts.)"
    }
} else { Write-Host "Firewall rule for 51338 already present." -ForegroundColor Green }

# --- Start the server: NO bot, shuffled ---
Remove-Item Env:\PTO_BOT -ErrorAction SilentlyContinue
Remove-Item Env:\PTO_NOSHUFFLE -ErrorAction SilentlyContinue
Remove-Item ".\server_live.log" -ErrorAction SilentlyContinue
$srv = Start-Process ".\PtoServer.exe" -RedirectStandardOutput ".\server_live.log" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 1

$listening = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Measure-Object).Count
$ips = Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -notlike '192.168.56.*' } | Select-Object -ExpandProperty IPAddress

Write-Host ""
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338 (LAN, no bot)." -ForegroundColor Green }
else { Write-Host "Server did not start listening - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host "This HOST's LAN address(es) for the other device to use:" -ForegroundColor Cyan
foreach ($ip in $ips) { Write-Host "    $ip" }
Write-Host ""
Write-Host "On the OTHER device: set  Game\settings.ini  ->  [NETWORK] IP=<the address above>"
Write-Host "On THIS device: the client can use 127.0.0.1 (or the address above)."
Write-Host "Both players: log in with DIFFERENT usernames, ARENA -> READY. You'll match each other."
Write-Host ""
Write-Host "Watch:  Get-Content .\server_live.log -Wait"
