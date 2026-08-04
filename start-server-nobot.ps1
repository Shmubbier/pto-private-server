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

# Build the test deck: leader + 30 hero cards (the client requires 30 to start a match). To keep the
# deck to ONLY finished cards, the 17 finished heroes are repeated (duplicates) to fill 30 slots.
# Card id = REAL * 2. If the Arena deck-select screen rejects duplicates, fall back to filling with
# other heroes instead. Excluded (unfinished): Fighter, Dragon Mage, Mystic, Oracle, Homunculus,
# Paladin, Planestalker, Force Cube, Zombie, Fire/Water/Air/Dark Elemental.
New-Item -ItemType Directory -Force -Path ".\data" | Out-Null
# Ordered so the cards under test come FIRST. With PTO_NOSHUFFLE=1 (set below) the deck stays in this
# order, so the opening hand is the first 5 heroes here. Reorder to test different cards.
$finished = @(
    60,   # 30 Alchemist   <- aura: Leader: Armor 2 (Flank)
    62,   # 31 Gunner      <- aura: Supporter: R.Attack (Flank)
    96,   # 48 Trapper     <- aura: Forerunner: Intercept, Supporter: R.Attack (Flank)
    98,   # 49 Vampire     <- Vamp / Cover: Forerunner
    70,   # 35 Knight      <- Counter / Cover: Forerunner (Rear)
    88,   # 44 Scientist   <- Haste (order) / Draw (flank spell)
    58,   # 29 Assassin    <- Hero Killer / Assassinate
    72,   # 36 Mascot      <- Draw (order) / Inspire
    64,   # 32 Healer      <- Armor / Intercept
    56,   # 28 Berserker
    60,   # 30 Alchemist
    62,   # 31 Gunner
    68,   # 34 Illusionist
    78,   # 39 Overlord
    84,   # 42 Priestess
    86,   # 43 Pyromancer
    92,   # 46 Summoner
    94,   # 47 Templar
    96,   # 48 Trapper
    100   # 50 Witch
)
# Repeat the finished list until we have 30 hero cards.
$heroes = @()
while ($heroes.Count -lt 30) { $heroes += $finished }
$heroes = $heroes[0..29]
$cards = @(2) + $heroes
$deck = "0|0|1|2|" + ($cards -join ',') + "|Arena Deck"
# Give every account we might log in as the same finished deck.
Set-Content ".\data\tester.decks" -Value $deck -Encoding utf8
Set-Content ".\data\fester.decks" -Value $deck -Encoding utf8
Set-Content ".\data\jester.decks" -Value $deck -Encoding utf8

# Start the server only (logs to server_live.log). No bot.
# PTO_NOSHUFFLE keeps the deck in the fixed order above so the opening hand is deterministic for testing.
$env:PTO_NOSHUFFLE = "1"
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
