# Dedicated test launcher for the ONGOING abilities (Immortality, Restructure). Rebuilds PtoServer.exe
# first (so the code is live), seeds the account "ongoing" with focused decks, and starts the server
# (solo vs bot). Card id = REAL * 2. PTO_NOSHUFFLE fixes deck order so the front-loaded heroes land in
# the opening hand. Log in as username "ongoing" (any password) and pick a deck in-game.
#
# IMMORTALITY (Curse Knight order, costs 3 ORBS): for 3 waves your VANGUARD (front-row) heroes can't
#   die by any means. Orbs are +1/wave (cap 3) from round 2, so you can first afford it around round 2's
#   Rear wave / round 3. Summon a couple of vanguard bodies, cast it, then let the bot attack them - they
#   survive at 1 HP (watch the log for "IMMORTAL: ... survives lethal").
# RESTRUCTURE (Homunculus order / Bannerman flank-spell or order): for the rest of your turn, MOVE and
#   CLEAR CORPSE cost no action. Cast it, then move units around and watch your action count NOT drop.
# Both are solo-testable vs the bot.
Set-Location $PSScriptRoot

# 1) Free the port + exe lock, then rebuild.
$holder = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
if ($holder) { Write-Host "Killing old server on port 51338 (PID $holder)..."; Stop-Process -Id $holder -Force -ErrorAction SilentlyContinue }
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

Write-Host "Rebuilding PtoServer.exe..."
& (Join-Path $PSScriptRoot "build.ps1")
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED - not starting." -ForegroundColor Red; exit 1 }

# 2) Decks. Card ids: Curse Knight 122, Homunculus 66, Bannerman 190.
$decks = @(
    @{ name = "1 Immortality";  front = @(122) }             # Curse Knight order (3 orbs): vanguard can't die for 3 waves.
    @{ name = "2 Restructure";  front = @(66,190) }          # Homunculus order / Bannerman (F spell + order): free move + clear-corpse this turn.
    @{ name = "3 Both Ongoing"; front = @(122,66,190) }      # all three ongoing heroes in the opening hand.
)
$filler = @(56,58,60,62,64,68,70,72,78,84,86,88,92,94,96,98,100)   # finished heroes to fill 30

function Build-DeckLines($deckSet) {
    for ($id = 0; $id -lt $deckSet.Count; $id++) {
        $heroes = @($deckSet[$id].front) + $filler
        while ($heroes.Count -lt 30) { $heroes += $filler }
        $heroes = $heroes[0..29]
        $cards = (@(2) + $heroes) -join ','      # cards[0] = leader (card 2), then 30 heroes
        "$id|0|1|2|$cards|$($deckSet[$id].name)" # Id|Flag|Back|Land|cards|Name
    }
}

New-Item -ItemType Directory -Force -Path ".\data" | Out-Null
Set-Content ".\data\ongoing.decks" -Value ((Build-DeckLines $decks) -join "`n") -Encoding utf8

# Bot: a plain finished-hero deck (bodies to attack your vanguard).
$botCards = (@(2) + ($filler + $filler)[0..29]) -join ','
Set-Content ".\data\bot.decks" -Value "0|0|1|2|$botCards|Bot Deck" -Encoding utf8

# 3) Start the server.
$env:PTO_BOT = "1"
$env:PTO_NOSHUFFLE = "1"
$env:PTO_EFXID = ""
Remove-Item ".\server_live.log" -ErrorAction SilentlyContinue
$srv = Start-Process ".\PtoServer.exe" -RedirectStandardOutput ".\server_live.log" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 1

$listening = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host ""
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338 (fresh build). Account 'ongoing' seeded with 3 decks." -ForegroundColor Green }
else { Write-Host "Server did not start - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host "Log in as username 'ongoing' (any password), then pick a deck:"
for ($id = 0; $id -lt $decks.Count; $id++) { Write-Host ("  " + $decks[$id].name) }
Write-Host ""
Write-Host "Immortality: summon vanguard bodies, reach 3 orbs (~round 2 Rear), play Curse Knight's ORDER,"
Write-Host "  end turn; the bot's attacks on your vanguard leave them at 1 HP (log: 'IMMORTAL: ... survives')."
Write-Host "Restructure: play Homunculus's ORDER (or Bannerman), then move/clear-corpse - your action count"
Write-Host "  does not drop (log: 'RESTRUCTURE: move was free')."
Write-Host ""
Write-Host "Watch:  Get-Content .\server_live.log -Wait"
