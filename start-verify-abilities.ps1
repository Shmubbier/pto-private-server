# Focused launcher to verify the 3 not-yet-tested abilities: Transfusion, Enervate, Entomb.
# They live on two heroes, so the deck alternates them (Diabloist 124 / Homunculus 66) to fill 30 --
# the opening hand is 124,66,124,66,124 = three Diabloists + two Homunculus, so everything is in hand.
# Rebuilds PtoServer.exe first, seeds account "verify", starts the server (solo vs bot).
#
# HOW TO TEST each (all are WAVE spells except Entomb which is also an order):
#   TRANSFUSION - summon Homunculus at REAR (wave 0). Let the bot damage one of your heroes, then cast
#     Homunculus's Rear spell on that damaged ally: it fully heals and Homunculus takes that damage.
#     Log: "TRANSFUSION: healed N ...".
#   ENERVATE 3  - summon Diabloist at VANGUARD (wave 2). Cast its Vanguard spell on a bot hero; that
#     hero's attack number drops by 3. Log: "ENERVATE -3 attack -> enemy (...)".
#   ENTOMB      - play Diabloist's ORDER (Entomb 2) from hand targeting an empty ENEMY cell (needs the
#     bot to hold cards - it keeps ~3). The rival discards random card(s) that appear as corpse(s) in
#     the enemy's empty space(s). Log: "ENTOMB: ... -> corpse at enemy (...)". (Diabloist's Rear spell
#     is also Entomb 1 if you'd rather summon it at Rear.)
Set-Location $PSScriptRoot

$holder = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
if ($holder) { Write-Host "Killing old server on port 51338 (PID $holder)..."; Stop-Process -Id $holder -Force -ErrorAction SilentlyContinue }
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Write-Host "Rebuilding PtoServer.exe..."
& (Join-Path $PSScriptRoot "build.ps1")
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED - not starting." -ForegroundColor Red; exit 1 }

# Diabloist 124 (Enervate V / Entomb R + order), Homunculus 66 (Transfusion R), alternating to fill 30.
$pair = @(124,66)
$heroes = @()
while ($heroes.Count -lt 30) { $heroes += $pair }
$heroes = $heroes[0..29]
$cards = (@(2) + $heroes) -join ','   # cards[0] = leader (card 2), then 30 heroes

New-Item -ItemType Directory -Force -Path ".\data" | Out-Null
Set-Content ".\data\verify.decks" -Value "0|0|1|2|$cards|Transf-Enerv-Entomb" -Encoding utf8

# Bot: finished heroes as damageable bodies + hand cards to Entomb.
$filler = @(56,58,60,62,64,68,70,72,78,84,86,88,92,94,96,98,100)
$botCards = (@(2) + ($filler + $filler)[0..29]) -join ','
Set-Content ".\data\bot.decks" -Value "0|0|1|2|$botCards|Bot Deck" -Encoding utf8

$env:PTO_BOT = "1"
$env:PTO_NOSHUFFLE = "1"
$env:PTO_EFXID = ""
Remove-Item ".\server_live.log" -ErrorAction SilentlyContinue
$srv = Start-Process ".\PtoServer.exe" -RedirectStandardOutput ".\server_live.log" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 1

$listening = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host ""
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338 (fresh build). Account 'verify' seeded." -ForegroundColor Green }
else { Write-Host "Server did not start - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host "Log in as username 'verify' (any password), pick 'Transf-Enerv-Entomb'."
Write-Host "Opening hand: Diabloist, Homunculus, Diabloist, Homunculus, Diabloist."
Write-Host "Transfusion = Homunculus REAR spell | Enervate = Diabloist VANGUARD spell | Entomb = Diabloist ORDER (or REAR spell)."
Write-Host "Watch:  Get-Content .\server_live.log -Wait"
