# Gen-2 leader-active test launcher. One deck per NEWLY-implemented leader (card[0] = leader card id = REAL*2)
# so you can pick a deck in-game and try that leader. Account "leader3" holds all 12. Rebuilds the exe first.
# Watch server_live.log for "LEADER (Name): ..." lines.
#
# What to look for per leader:
#   Merjoram  - cast: give a hero Shield, this leader loses 2 life.
#   Cague     - your VANGUARD heroes gain Intercept + Deathproof (aura; summon a vanguard hero and check).
#   Andrus    - after YOUR hero attacks: melee -> Ice 2 on target + adjacent; ranged -> Fire 2 on target's row.
#   Luca      - cast: Phantom (return a random discard card to hand).
#   Rayne     - cast: defeat a DAMAGED enemy hero; you discard a random card.
#   Eligor    - your heroes gain Revenge: Strength 5 (aura).
#   Uriah     - leader CANNOT attack; takes at most 4 damage per attack; counters melee attackers.
#   Ivo       - cast: give a hero Strength 2; this leader's Strength = number of your heroes.
#   Lizaveta  - cast: give a friendly hero Ranged Attack (persistent).
#   Riflam    - cast: transform target hero into a random hero (enters full life).
#   Baenvier  - cast: Silence an enemy hero; this leader gains +1 Strength.
#   Malandrax - cast: discard a random card, gain 1 orb (orb cap is 6 for Malandrax).
Set-Location $PSScriptRoot

$holder = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
if ($holder) { Write-Host "Killing old server on port 51338 (PID $holder)..."; Stop-Process -Id $holder -Force -ErrorAction SilentlyContinue }
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Write-Host "Rebuilding PtoServer.exe..."
& (Join-Path $PSScriptRoot "build.ps1")
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED - not starting." -ForegroundColor Red; exit 1 }

# leader card id = REAL * 2.
$acct = @(
    @{ name = "1 Merjoram (shield, -2 life)";     id = 140 }
    @{ name = "2 Cague (Vanguard Intercept+Dp)";  id = 142 }
    @{ name = "3 Andrus (attack->Ice/Fire)";      id = 144 }
    @{ name = "4 Luca (Phantom)";                 id = 146 }
    @{ name = "5 Rayne (kill damaged, discard)";  id = 148 }
    @{ name = "6 Eligor (Revenge Str5)";          id = 150 }
    @{ name = "7 Uriah (no-attack, cap4, ctr)";   id = 152 }
    @{ name = "8 Ivo (Str2, Str=#heroes)";        id = 154 }
    @{ name = "9 Lizaveta (grant R.Attack)";      id = 156 }
    @{ name = "10 Riflam (Polymorph)";            id = 158 }
    @{ name = "11 Baenvier (Silence, +1 Str)";    id = 198 }
    @{ name = "12 Malandrax (discard, +orb, 6cap)";id = 222 }
)
$filler = @(56,58,60,62,64,68,70,72,78,84,86,88,92,94,96,98,100)
$heroes = @(); while ($heroes.Count -lt 30) { $heroes += $filler }
$heroes = $heroes[0..29]
$heroCsv = $heroes -join ','

$decks = for ($i = 0; $i -lt $acct.Count; $i++) { "$i|0|1|2|$($acct[$i].id),$heroCsv|$($acct[$i].name)" }

New-Item -ItemType Directory -Force -Path ".\data" | Out-Null
Set-Content ".\data\leader3.decks" -Value ($decks -join "`n") -Encoding utf8

# Bot: finished heroes as bodies/targets (its leader is the default card 2).
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
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338 (fresh build). Account 'leader3' seeded with 12 gen-2 leaders." -ForegroundColor Green }
else { Write-Host "Server did not start - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host "Log in as 'leader3' (any password). Pick a deck per leader to test it."
Write-Host "Watch:  Get-Content .\server_live.log -Wait"
