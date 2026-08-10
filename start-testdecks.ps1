# Seeds ONE account ("tester") with all 12 ability-test decks, then starts the server (solo vs bot).
# Log in as username "tester" (any password) and pick a deck IN-GAME per ability -- no server restart
# needed to switch. PTO_NOSHUFFLE keeps each deck's order fixed so its front-loaded ability hero(es)
# land in the opening hand. Card id = REAL * 2.  Re-run this to regenerate the decks after new abilities.
Set-Location $PSScriptRoot

$holder = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
if ($holder) { Write-Host "Killing old server on port 51338 (PID $holder)..."; Stop-Process -Id $holder -Force -ErrorAction SilentlyContinue }
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# Deck slot 0..11 : display name : hero card ids front-loaded into the opening hand.
$decks = @(
    @{ name = "1 Polymorph";      front = @(176) }                         # Biomancer: order (& V/F spell) transform an ENEMY hero into a random one
    @{ name = "2 Copycat";        front = @(128,228) }                     # Doppelganger(V Quick spell) / Ghost(F spell): caster becomes a copy of an ENEMY hero
    @{ name = "3 Seance";         front = @(100,124) }                     # Witch(R spell) / Diabloist(F spell): return YOUR corpse to hand
    @{ name = "4 Replicate";      front = @(130,128) }                     # Puppeteer(order, Quick) / Doppelganger(F spell): copy a FRIENDLY hero's card to hand
    @{ name = "5 Infusion";       front = @(120,212) }                     # Chronicler(V spell: leader +N, self -N; order Infuse Leader 4) / Statistician(V Infuse Leader 3, R Meteor Storm 7)
    @{ name = "6 Execute";        front = @(54,136) }                      # Dragon Mage(V spell) / Sniper(F spell): instantly defeat an enemy hero
    @{ name = "7 Meteor Storm";   front = @(138,212) }                     # Warmage(order: 10 to all rivals) / Statistician(R spell: 7 to all rivals)
    @{ name = "8 Shield/Str/Sil"; front = @(210,192,54,170) }             # Scholar/Squire(Shield) / Dragon Mage(Str Buff 5 order) / Wizard(Str/Silence)
    @{ name = "9 Decoy/Swap/Take";front = @(220,108,194) }                 # Occultist(Decoy) / Air Elemental(R Swap) / Dark Knight(R Takedown)
    @{ name = "10 Fin/Ice/Bk/Orb";front = @(172,164,174,224) }             # Lancer(Finisher) / Ranger(Ice) / Reflector(Backlash) / Mastermind(Orbs)
    @{ name = "11 Ban/Uns/Ret/Wl";front = @(96,92,68,110) }                # Trapper(Banish) / Summoner(Unsummon) / Illusionist(Retreat/Wild) / Dark Elemental(Unsummon)
    @{ name = "12 Meteor/Back";   front = @(210,208,138) }                 # Scholar(Meteor 6 order) / Magic Student(Quick Meteor 2) / Warmage(Meteor 7)
)
$filler = @(56,58,60,62,64,68,70,72,78,84,86,88,92,94,96,98,100)   # finished heroes to fill 30

New-Item -ItemType Directory -Force -Path ".\data" | Out-Null
$lines = for ($id = 0; $id -lt $decks.Count; $id++) {
    $heroes = @($decks[$id].front) + $filler
    while ($heroes.Count -lt 30) { $heroes += $filler }
    $heroes = $heroes[0..29]
    $cards = (@(2) + $heroes) -join ','      # cards[0] = leader (card 2), then 30 heroes
    "$id|0|1|2|$cards|$($decks[$id].name)"   # Id|Flag|Back|Land|cards|Name
}
Set-Content ".\data\tester.decks" -Value ($lines -join "`n") -Encoding utf8

# Bot: a plain finished-hero deck for board bodies / targets.
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
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338. Account 'tester' seeded with 12 decks." -ForegroundColor Green }
else { Write-Host "Server did not start - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host "Log in as username 'tester' (any password), then pick a deck in-game:"
for ($id = 0; $id -lt $decks.Count; $id++) { Write-Host ("  " + $decks[$id].name) }
Write-Host "Switch decks between matches with no restart. Watch:  Get-Content .\server_live.log -Wait"
