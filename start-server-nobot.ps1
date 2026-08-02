# Starts ONLY the PTO private server (no test bot) — for human-vs-human testing.
# Run this, then launch the game on each client (tester on one, fester on the other) and READY up.
Set-Location $PSScriptRoot

# Stop any previous server + any lingering test bot
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like '*testbot*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

# Build if the exe is missing
if (-not (Test-Path ".\PtoServer.exe")) {
    Write-Host "Building server..."
    & "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /out:PtoServer.exe PtoServer.cs | Out-Null
}

# Make sure the 'tester' account has a ready 31-card deck to select in Arena
New-Item -ItemType Directory -Force -Path ".\data" | Out-Null
$cards = @(2) + (0..29 | ForEach-Object { 52 + $_ * 2 })
Set-Content ".\data\tester.decks" -Value ("0|0|1|2|" + ($cards -join ',') + "|Arena Deck") -Encoding utf8
# and the same for 'fester' (second human) so both have a legal deck
Set-Content ".\data\fester.decks" -Value ("0|0|1|2|" + ($cards -join ',') + "|Arena Deck") -Encoding utf8

# Start the server only (logs to server_live.log). No bot.
Remove-Item ".\server_live.log" -ErrorAction SilentlyContinue
$srv = Start-Process ".\PtoServer.exe" -RedirectStandardOutput ".\server_live.log" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 1

$listening = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Measure-Object).Count
"$($srv.Id)" | Set-Content ".\live_pids.txt"

Write-Host ""
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338 (NO bot - human vs human)." -ForegroundColor Green }
else { Write-Host "Server did not start listening - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host "Now play (two clients):"
Write-Host "  1. Launch the game on each machine/instance"
Write-Host "  2. Log in as 'tester' on one and 'fester' on the other (both have an Arena Deck)"
Write-Host "  3. Both click ARENA -> READY; you'll match each other"
Write-Host ""
Write-Host "Watch the server:  Get-Content .\server_live.log -Wait"
Write-Host "Stop everything:   .\stop-server.ps1"
