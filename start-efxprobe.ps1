# Effect-id calibration launcher (solo vs bot). Sets PTO_EFXID so every order/spell you cast ALSO fires
# container_effect (op41) with that effect id along the REAL trajectory: from the caster (your leader for
# orders, the casting hero for wave-spells) to the cell you targeted on the enemy board.
#
# The eid -> object-index table is known (effects_init/add_effect). Only the "themed" effect objects
# release next_que (via can_unque_same_effect); the generic ones FREEZE. Two-phase calibration:
#
# PHASE 1 (DONE): projectile eids (fromto=1) = {0,12,16,21,23}.  eid 0 = obj_single_arrow_target = the
#   queue-safe single-target arrow. Already locked in for single-target damage (Meteor/Ice/Backlash/...).
#
# PHASE 2 (NOW): the AT-TARGET splash eids (fromto=0) for the AoE/heal shapes. Sweep these and note which
#   plays a fire / ice / thunder / poison / cure-all / mass-effect splash AND lets you keep playing:
#     1 2 3 4 5 6 7 8 9 10 11 13 14 15 17 18 19 20 22 24 25
#   (eid -> objIndex, for correlating your findings: 1->44 2->43 3->45 4->46 5->47 6->48 7->49 8->50
#    9->52 10->51 11->53 13->55 14->56 15->57 17->58 18->60 19->62 20->61 22->63 24->79 25->59)
#   Most will FREEZE (generic obj_damage_effect). The ~7 KEEPERS are obj_single_poison / obj_second_ice /
#   obj_second_thunder / obj_quick_fire / obj_many_heal / obj_many_ress / obj_mass_str_up.
#
#   .\start-efxprobe.ps1 1      # then 2, 3, 4, ... 25 (skip 0,12,16,21,23 - already known)
#
# For each: cast a DAMAGE order at an enemy. Splash plays + you can continue = KEEPER (tell me the eid +
# what it looked like: fire? ice? thunder? poison?). Freeze = BAD, next id. I'll map each shape to its eid.
param([int]$EfxId = 0)
Set-Location $PSScriptRoot

$holder = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
if ($holder) { Write-Host "Killing old server on port 51338 (PID $holder)..."; Stop-Process -Id $holder -Force -ErrorAction SilentlyContinue }
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$finished = @(68,92,78,100,84, 56,58,60,62,64,70,72,86,88,94,96,98)  # Illusionist,Summoner,Overlord,Witch,Priestess first
$heroes = @(); while ($heroes.Count -lt 30) { $heroes += $finished }
$heroes = $heroes[0..29]
$deck = "0|0|1|2|" + (((@(2) + $heroes)) -join ',') + "|Arena Deck"

New-Item -ItemType Directory -Force -Path ".\data" | Out-Null
Set-Content ".\data\bot.decks"    -Value $deck -Encoding utf8
Set-Content ".\data\tester.decks" -Value $deck -Encoding utf8
Set-Content ".\data\player.decks" -Value $deck -Encoding utf8

$env:PTO_BOT = "1"
$env:PTO_NOSHUFFLE = "1"
$env:PTO_EFXID = "$EfxId"
Remove-Item ".\server_live.log" -ErrorAction SilentlyContinue
$srv = Start-Process ".\PtoServer.exe" -RedirectStandardOutput ".\server_live.log" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 1

$listening = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host ""
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338 (BOT enabled, PTO_EFXID=$EfxId)." -ForegroundColor Green }
else { Write-Host "Server did not start listening - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host "PHASE 2 sweep (at-target splashes). Cast a DAMAGE order at an ENEMY hero; it fires eid $EfxId."
Write-Host "  - Splash plays (fire/ice/thunder/poison?) AND you can keep playing -> KEEPER (note the look)"
Write-Host "  - Game freezes / stuck after casting                              -> BAD (next id)"
Write-Host "Sweep:  .\start-efxprobe.ps1 2   (then 3,4,5,6,7,8,9,10,11,13,14,15,17,18,19,20,22,24,25)"
