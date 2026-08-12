# Leader-passive test launcher. Each deck uses a different LEADER (card[0] = leader card id = REAL*2)
# so you can pick a deck in-game to try that leader's passive. 17 leaders are implemented; the per-account
# cap is 12, so they're split across two accounts: "leader" (12) and "leader2" (5). Every deck is 30
# finished heroes (which include order/spell heroes) so all triggers are reachable. Rebuilds the exe first.
#
# What to look for per leader (watch server_live.log for "LEADER (Name): ..." lines):
#   Hikaru/Cadenza/Borneo/Demitras/Eligor/Zaamassal/Kallistar - summon heroes; their stats/abilities gain
#     the aura (attack/armor/Swift/Vamp/Revenge/R.Attack; Kallistar = Str3 if you're first player else Armor1).
#   Kavri - end your turn with damaged heroes in the current wave; they heal 2.  Marmelee - end turn with <5 cards; draw 1.
#   Rukyuk - play any ORDER; a random rival hero takes 3.   Tatsumi - your FIRST order each turn draws 1 + gives an action.
#   Lixis - at end of round, all rival heroes take 3 and the rival leader takes 1.   Mikhail - end of round revives one of your corpses.
#   Sagas - end of round copies a random own hero's card to your hand.   Magdelina - end of round, +1 Strength to all yours (stacks).
#   Lesandra - summoning costs no action.   Star Knight Iri - each time your hero dies, your leader gains +1 attack and heals 3.
Set-Location $PSScriptRoot

$holder = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
if ($holder) { Write-Host "Killing old server on port 51338 (PID $holder)..."; Stop-Process -Id $holder -Force -ErrorAction SilentlyContinue }
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Write-Host "Rebuilding PtoServer.exe..."
& (Join-Path $PSScriptRoot "build.ps1")
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED - not starting." -ForegroundColor Red; exit 1 }

# leader card id = REAL * 2.
$acct1 = @(
    @{ name = "1 Hikaru (Melee Str2 aura)";   id = 12 }
    @{ name = "2 Cadenza (Armor1 aura)";      id = 6 }
    @{ name = "3 Borneo (Swift aura)";        id = 24 }
    @{ name = "4 Demitras (Vamp aura)";       id = 44 }
    @{ name = "5 Eligor (Revenge Str5)";      id = 40 }
    @{ name = "6 Zaamassal (R.Attack aura)";  id = 34 }
    @{ name = "7 Kallistar (Str3/Armor1)";    id = 8 }
    @{ name = "8 Kavri (end-turn heal)";      id = 16 }
    @{ name = "9 Marmelee (end-turn draw)";   id = 48 }
    @{ name = "10 Rukyuk (order Bombard3)";   id = 14 }
    @{ name = "11 Tatsumi (1st order draw)";  id = 26 }
    @{ name = "12 Lixis (end-round AoE)";     id = 2 }
)
$acct2 = @(
    @{ name = "1 Mikhail (end-round revive)"; id = 20 }
    @{ name = "2 Sagas (end-round Replicate)";id = 28 }
    @{ name = "3 Magdelina (+1 Str/round)";   id = 36 }
    @{ name = "4 Lesandra (free recruit)";    id = 22 }
    @{ name = "5 StarKnightIri (on-defeat)";  id = 50 }
    @{ name = "6 Shekhtur (defeat->attack)";  id = 4 }
    @{ name = "7 Arec (random retarget)";     id = 18 }
    @{ name = "8 Seth (wavespell->Scry)";     id = 30 }
    @{ name = "9 Rexan (enemy die->Zombie)";  id = 32 }
    @{ name = "10 Luc (leader Haste)";        id = 38 }
    @{ name = "11 Khadath (leader Banish)";   id = 42 }
    @{ name = "12 Hepzibah (leader RaiseDead)";id = 46 }
)
$filler = @(56,58,60,62,64,68,70,72,78,84,86,88,92,94,96,98,100)
$heroes = @(); while ($heroes.Count -lt 30) { $heroes += $filler }
$heroes = $heroes[0..29]
$heroCsv = $heroes -join ','

function Build-LeaderDecks($set) {
    for ($i = 0; $i -lt $set.Count; $i++) { "$i|0|1|2|$($set[$i].id),$heroCsv|$($set[$i].name)" }
}

New-Item -ItemType Directory -Force -Path ".\data" | Out-Null
Set-Content ".\data\leader.decks"  -Value ((Build-LeaderDecks $acct1) -join "`n") -Encoding utf8
Set-Content ".\data\leader2.decks" -Value ((Build-LeaderDecks $acct2) -join "`n") -Encoding utf8

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
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338 (fresh build). Accounts 'leader' (12) + 'leader2' (5) seeded." -ForegroundColor Green }
else { Write-Host "Server did not start - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host "Log in as 'leader' for 12 leaders, or 'leader2' for the other 5 (any password). Pick a deck per leader."
Write-Host "Watch:  Get-Content .\server_live.log -Wait"
