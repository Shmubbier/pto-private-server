# Ability-test launcher: like start-playtest.ps1 (solo vs bot) but the deck front-loads the heroes
# whose NEW abilities aren't in the normal Arena deck, so they land in your opening hand:
#   Lancer(172)=Finisher Kill,  Ranger(164)=Ice,  Scholar(210)=Meteor 6,  Mastermind(224)=Orb Boost/Drain,
#   Reflector(174)=Backlash,  Warmage(138)=Meteor 7,  Magic Student(208)=Quick Meteor 2.
# PTO_NOSHUFFLE keeps the order fixed so the front cards are dealt first.
#
# HOW TO TEST each (card id = REAL*2):
#   Finisher Kill (Lancer ORDER, or Rear spell): defeat an ALREADY-DAMAGED enemy hero (does nothing on a full-HP one).
#   Ice 4 (Ranger ORDER): damages a chosen enemy hero + its 4 adjacent cells.
#   Meteor 6 (Scholar ORDER) / Meteor 7 (Warmage REAR spell) / Quick Meteor 2 (Magic Student REAR spell): damage one enemy hero.
#   Backlash (Reflector REAR spell): deal an enemy hero damage equal to its OWN attack.
#   Orb Boost (Mastermind VANGUARD spell): +1 your orb.   Orb Drain (Mastermind REAR spell): -1 rival orb.
# Orders are played straight from hand; spells require summoning the hero to that wave, then casting next turn.
# Watch server_live.log for FINISHER/Backlash/ICE/ORB lines and the damage lines.
Set-Location $PSScriptRoot

$holder = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
if ($holder) { Write-Host "Killing old server on port 51338 (PID $holder)..."; Stop-Process -Id $holder -Force -ErrorAction SilentlyContinue }
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# Front 7 = new-ability heroes (dealt first); then finished heroes for board bodies / bot targets.
$test   = @(172,164,210,224,174,138,208)
$filler = @(56,58,60,62,64,68,70,72,78,84,86,88,92,94,96,98,100)
$heroes = @($test) + $filler
while ($heroes.Count -lt 30) { $heroes += $filler }
$heroes = $heroes[0..29]
$deck = "0|0|1|2|" + (((@(2) + $heroes)) -join ',') + "|Arena Deck"

New-Item -ItemType Directory -Force -Path ".\data" | Out-Null
Set-Content ".\data\bot.decks"    -Value $deck -Encoding utf8
Set-Content ".\data\tester.decks" -Value $deck -Encoding utf8
Set-Content ".\data\player.decks" -Value $deck -Encoding utf8

$env:PTO_BOT = "1"
$env:PTO_NOSHUFFLE = "1"
$env:PTO_EFXID = ""   # effect probe OFF for ability testing
Remove-Item ".\server_live.log" -ErrorAction SilentlyContinue
$srv = Start-Process ".\PtoServer.exe" -RedirectStandardOutput ".\server_live.log" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 1

$listening = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host ""
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338 (BOT enabled, ability-test deck)." -ForegroundColor Green }
else { Write-Host "Server did not start listening - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host "Opening hand front-loads: Lancer(Finisher Kill), Ranger(Ice), Scholar(Meteor 6),"
Write-Host "  Mastermind(Orb Boost/Drain), Reflector(Backlash), Warmage(Meteor 7), Magic Student(Quick Meteor 2)."
Write-Host "Orders play from hand; spells need the hero summoned to that wave first. See script header for how-to."
Write-Host "Watch:  Get-Content .\server_live.log -Wait"
