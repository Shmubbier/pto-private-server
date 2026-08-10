# Parameterized ability-test launcher. Pass a deck NAME; it front-loads that ability's hero(es) into
# your opening hand (bot uses the same deck, so it fields those heroes as targets). PTO_NOSHUFFLE keeps
# the order fixed so the front cards are dealt first. Card id = REAL * 2.
#
#   .\start-deck.ps1 finisher      # test one ability
#   .\start-deck.ps1               # 'all' -> every new-ability hero
#
# A new named deck is added here for each newly-implemented ability. Current decks:
param([string]$Deck = "all")
Set-Location $PSScriptRoot

# name -> heroes front-loaded (card id = REAL*2), and a one-line how-to shown at launch.
$decks = @{
    "finisher" = @{ cards = @(172);          how = "Lancer ORDER (or Rear spell): defeat an ALREADY-DAMAGED enemy hero (no-op on full HP)." }
    "ice"      = @{ cards = @(164);          how = "Ranger ORDER: click an enemy hero -> it + its 4 adjacent cells take 4." }
    "meteor"   = @{ cards = @(210,138,208);  how = "Scholar ORDER (Meteor 6) / Warmage Rear (Meteor 7) / Magic Student Rear (Quick Meteor 2): 1 enemy hero." }
    "backlash" = @{ cards = @(174);          how = "Reflector Rear spell: enemy hero takes damage = its OWN attack." }
    "orbs"     = @{ cards = @(224);          how = "Mastermind Vanguard spell = Orb Boost (+1 yours); Rear spell = Orb Drain (-1 rival)." }
    "banish"   = @{ cards = @(96,68);        how = "Trapper ORDER (Banish 2) / Rear spell (Banish 1); Illusionist Flank spell (Banish 1): rival discards random." }
    "unsummon" = @{ cards = @(92,110);       how = "Summoner Vanguard spell / Dark Elemental ORDER: return a NON-INTERCEPT enemy hero to rival hand." }
    "retreat"  = @{ cards = @(68);           how = "Illusionist Vanguard spell: return one of YOUR heroes to your hand." }
    "wildsummon"= @{ cards = @(68);          how = "Illusionist ORDER: steal + play a random card from the rival's hand, then discard it." }
    "all"      = @{ cards = @(172,164,210,224,174,138,208); how = "Every NEW-ability hero: Finisher Kill, Ice, Meteor x3, Orb Boost/Drain, Backlash." }
}

if (-not $decks.ContainsKey($Deck)) {
    Write-Host "Unknown deck '$Deck'. Available:" -ForegroundColor Yellow
    $decks.Keys | Sort-Object | ForEach-Object { Write-Host "  $_" }
    return
}

$holder = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
if ($holder) { Write-Host "Killing old server on port 51338 (PID $holder)..."; Stop-Process -Id $holder -Force -ErrorAction SilentlyContinue }
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$filler = @(56,58,60,62,64,68,70,72,78,84,86,88,92,94,96,98,100)   # finished heroes for board bodies / bot targets
$heroes = @($decks[$Deck].cards) + $filler
while ($heroes.Count -lt 30) { $heroes += $filler }
$heroes = $heroes[0..29]
$deckStr = "0|0|1|2|" + (((@(2) + $heroes)) -join ',') + "|Arena Deck"

New-Item -ItemType Directory -Force -Path ".\data" | Out-Null
Set-Content ".\data\bot.decks"    -Value $deckStr -Encoding utf8
Set-Content ".\data\tester.decks" -Value $deckStr -Encoding utf8
Set-Content ".\data\player.decks" -Value $deckStr -Encoding utf8

$env:PTO_BOT = "1"
$env:PTO_NOSHUFFLE = "1"
$env:PTO_EFXID = ""
Remove-Item ".\server_live.log" -ErrorAction SilentlyContinue
$srv = Start-Process ".\PtoServer.exe" -RedirectStandardOutput ".\server_live.log" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 1

$listening = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host ""
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338 - deck '$Deck'." -ForegroundColor Green }
else { Write-Host "Server did not start - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host ("TEST: " + $decks[$Deck].how)
Write-Host "Orders play from hand; spells need the hero summoned to that wave first."
Write-Host "Watch:  Get-Content .\server_live.log -Wait"
