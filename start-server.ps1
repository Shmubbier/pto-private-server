# Starts the PTO private server + a test-bot opponent, then tells you how to play.
# Run this, then launch the game (PTO_C.exe) and log in.
Set-Location $PSScriptRoot

# stop any previous instances
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like '*testbot*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

# build if the exe is missing
if (-not (Test-Path ".\PtoServer.exe")) {
    Write-Host "Building server..."
    & "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /out:PtoServer.exe PtoServer.cs | Out-Null
}

# make sure the 'tester' account has a ready 31-card deck to select in Arena
New-Item -ItemType Directory -Force -Path ".\data" | Out-Null
$cards = @(2) + (0..29 | ForEach-Object { 52 + $_ * 2 })
Set-Content ".\data\tester.decks" -Value ("0|0|1|2|" + ($cards -join ',') + "|Arena Deck") -Encoding utf8

# start server (logs to server_live.log) + bot opponent
Remove-Item ".\server_live.log" -ErrorAction SilentlyContinue
$srv = Start-Process ".\PtoServer.exe" -RedirectStandardOutput ".\server_live.log" -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 1000
$bot = Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','.\testbot.ps1' -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

$listening = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Measure-Object).Count
"$($srv.Id) $($bot.Id)" | Set-Content ".\live_pids.txt"

Write-Host ""
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338 (bot opponent queued)." -ForegroundColor Green }
else { Write-Host "Server did not start listening - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host "Now play:"
Write-Host "  1. Launch  C:\Users\Charles\Downloads\PTO_C151\PTO_C.exe"
Write-Host "  2. Click LOGIN (tester / secret are pre-filled)"
Write-Host "  3. Click ARENA, make sure the deck is selected, click READY"
Write-Host "  4. You'll match the bot -> board, mulligan, then your turn."
Write-Host ""
Write-Host "To watch what the server sees:  Get-Content .\server_live.log -Wait"
Write-Host "To stop everything:            .\stop-server.ps1"
