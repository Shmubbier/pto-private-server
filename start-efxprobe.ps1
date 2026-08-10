# Effect-id calibration launcher. Same as start-playtest.ps1 (solo vs bot) but sets PTO_EFXID so every
# order you cast ALSO fires container_effect (op41) with that effect id, from your leader to the ENEMY
# leader (center lane). Use it to find which eid is a real projectile that RELEASES the client queue.
#
#   .\start-efxprobe.ps1 0      # try effect id 0   (then 12, 16, 21, 23 -- the "travels-from-source" ids)
#
# Watch for: does a projectile/effect play toward the enemy leader, AND can you keep playing afterward?
# If the game FREEZES after casting (stuck, can't end turn), that eid's effect does NOT release the queue
# -- note it as bad, re-run with the next id. If it plays AND you can continue, that's a keeper. Tell me
# the id(s) that worked and what each looked like.
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
Write-Host "Cast any ORDER. It will fire effect id $EfxId at the enemy leader."
Write-Host "  - Effect plays AND you can keep playing  -> GOOD (note what it looked like)"
Write-Host "  - Game freezes / stuck after casting     -> BAD  (that eid does not release the queue)"
Write-Host "Re-run with the next id:  .\start-efxprobe.ps1 12   (then 16, 21, 23, then 1..11, 13..25)"
