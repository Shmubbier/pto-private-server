# Self-host playtest launcher: runs the server locally with the AI BOT enabled (PTO_BOT=1) so a
# single player can queue Arena and be matched against the bot after ~2.5s. Decks are shuffled
# (no PTO_NOSHUFFLE). Point the client at 127.0.0.1 via settings.ini -> [NETWORK] IP=127.0.0.1
Set-Location $PSScriptRoot

# Kill WHATEVER holds port 51338 (a renamed/zombie exe won't match by name), then any PtoServer by name.
$holder = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
if ($holder) { Write-Host "Killing old server on port 51338 (PID $holder)..."; Stop-Process -Id $holder -Force -ErrorAction SilentlyContinue }
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
# Confirm the port is actually free before relaunching.
if (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue) {
    Write-Host "WARNING: port 51338 still held - kill it in Task Manager before the new server can bind." -ForegroundColor Red
}

if (-not (Test-Path ".\PtoServer.exe")) {
    Write-Host "Building server..."
    & "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /out:PtoServer.exe PtoServer.cs | Out-Null
}

# Finished cards (card id = REAL * 2). Front-loaded with the cards under test so, with PTO_NOSHUFFLE,
# they land in the opening hand. Reorder to test different abilities.
$finished = @(68,92,78,100,84, 56,58,60,62,64,70,72,86,88,94,96,98)  # Illusionist,Summoner,Overlord,Witch,Priestess first
$heroes = @(); while ($heroes.Count -lt 30) { $heroes += $finished }
$heroes = $heroes[0..29]
$deck = "0|0|1|2|" + (((@(2) + $heroes)) -join ',') + "|Arena Deck"

New-Item -ItemType Directory -Force -Path ".\data" | Out-Null
# The AI bot needs its own deck (username 'bot'). Also seed a couple of player names to test with.
Set-Content ".\data\bot.decks"    -Value $deck -Encoding utf8
Set-Content ".\data\tester.decks" -Value $deck -Encoding utf8
Set-Content ".\data\player.decks" -Value $deck -Encoding utf8

# Enable the bot; FIX the deck order so the opening hand is deterministic for testing.
$env:PTO_BOT = "1"
$env:PTO_NOSHUFFLE = "1"
Remove-Item ".\server_live.log" -ErrorAction SilentlyContinue
$srv = Start-Process ".\PtoServer.exe" -RedirectStandardOutput ".\server_live.log" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 1

$listening = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host ""
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338 (BOT enabled)." -ForegroundColor Green }
else { Write-Host "Server did not start listening - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host "Play solo vs the bot:"
Write-Host "  1. settings.ini -> [NETWORK] IP=127.0.0.1"
Write-Host "  2. Launch the (patched) game, log in, pick the Arena Deck, click ARENA -> READY"
Write-Host "  3. After ~2.5s with no human in queue, you're matched against the BOT"
Write-Host ""
Write-Host "Watch:  Get-Content .\server_live.log -Wait"
