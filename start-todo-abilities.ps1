# Test launcher seeding ONE deck with every hero that still has an UNIMPLEMENTED ability, so you can
# cycle through them as those abilities get built. Rebuilds PtoServer.exe first, seeds account "todo",
# starts the server (solo vs bot). Card id = REAL * 2. PTO_NOSHUFFLE keeps deck order fixed.
# Log in as username "todo" (any password) and pick the "Unimplemented" deck.
#
# Cards in the deck and the ability that is NOT yet implemented on each:
#   76  Oracle          -> Reload (order), Scry 3 (F spell)
#   208 Magic Student   -> Reload (order), Quick Scry 2 (V spell)
#   130 Puppeteer       -> Mind Control (V spell)          [Replicate order already works]
#   128 Doppelganger    -> Duplicate (order)               [Copycat/Replicate/Swap already work]
#   224 Mastermind      -> Duplicate (F spell)             [Traps/Orb Boost/Drain already work]
#   66  Homunculus      -> Transfusion (R spell)           [Restructure order / auras already work]
#   124 Diabloist       -> Enervate 3 (V spell), Entomb (R spell + order)   [Seance F already works]
#   88  Scientist       -> Force Cube (V spell)            [Draw/Inspire/Haste already work]
#   90  Force Cube      -> (the 0/1 Intercept body summoned by Scientist's Force Cube spell)
#   92  Summoner        -> Scry 3 (R spell)                [Unsummon/Summon already work]
#   108 Air Elemental   -> Scry 4 (order)                  [Swap R / Ephemeral already work]
#   132 Relic Hunter    -> Scry 4 (Quick order)            [Deathproof aura / Strength already work]
#   170 Wizard          -> Phantom (R spell + order)       [Silence/Strength Buff already work]
#   220 Occultist       -> Phantom (R spell)               [Decoy/Bombard/Backstab already work]
# (Leaders Seth = Scry, Luca = Phantom are leader cards, not in this hero deck.)
Set-Location $PSScriptRoot

# Rebuild so the server matches source (harmless if unchanged).
$holder = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
if ($holder) { Write-Host "Killing old server on port 51338 (PID $holder)..."; Stop-Process -Id $holder -Force -ErrorAction SilentlyContinue }
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Write-Host "Rebuilding PtoServer.exe..."
& (Join-Path $PSScriptRoot "build.ps1")
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED - not starting." -ForegroundColor Red; exit 1 }

# Every hero with a pending ability, repeated to fill 30 (so every draw is a relevant unit).
$todo = @(76,208,130,128,224,66,124,88,90,92,108,132,170,220)
$heroes = @()
while ($heroes.Count -lt 30) { $heroes += $todo }
$heroes = $heroes[0..29]
$cards = (@(2) + $heroes) -join ','   # cards[0] = leader (card 2), then 30 heroes

New-Item -ItemType Directory -Force -Path ".\data" | Out-Null
Set-Content ".\data\todo.decks" -Value "0|0|1|2|$cards|Unimplemented" -Encoding utf8

# Bot: plain finished heroes as bodies/targets.
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
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338 (fresh build). Account 'todo' seeded with the 'Unimplemented' deck." -ForegroundColor Green }
else { Write-Host "Server did not start - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host "Log in as username 'todo' (any password), pick 'Unimplemented'. Opening hand = 76,208,130,128,224."
Write-Host "Draw to cycle into the rest. These heroes' pending abilities currently no-op (action refunded)."
Write-Host "Watch:  Get-Content .\server_live.log -Wait"
