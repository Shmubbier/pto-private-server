# Dedicated TRAP test launcher. Rebuilds PtoServer.exe (so the trap code is live — avoids the
# stale-exe trap), seeds two mirror accounts ("traps" and "traps2") with trap-focused decks, and
# starts the server (solo-vs-bot enabled). Card id = REAL * 2. PTO_NOSHUFFLE fixes deck order so the
# front-loaded trap/spell/order heroes land in the opening hand.
#
# HOW TRAPS WORK: a trap is played like an order (1 action + 1 orb) and arms a hidden reserve; it
# fires on the RIVAL's NEXT matching action (round >= 2 only — you can place but not activate during
# the round-1 ceasefire), negating it. The rival still spends their action.
#   Cancel Attack (Reflector) -> negate + Backlash the attacker (it takes its own attack as damage).
#   Cancel Spell  (Statistician) -> negate the rival's wave-spell; you draw 1.
#   Cancel Order  (Mastermind)   -> negate the rival's order.
#
# BOT LIMITATION: the bot only summons/attacks/draws. So SOLO vs bot only Cancel Attack fires.
# Cancel Spell / Cancel Order need a 2nd HUMAN: run this once, have player A log in as "traps" and
# player B as "traps2", both pick "4 All Traps", and QUEUE within ~2s of each other so they match each
# other instead of the bot.
Set-Location $PSScriptRoot

# 1) Free the port + exe lock, then rebuild so the running server can't be a stale build.
$holder = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
if ($holder) { Write-Host "Killing old server on port 51338 (PID $holder)..."; Stop-Process -Id $holder -Force -ErrorAction SilentlyContinue }
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

Write-Host "Rebuilding PtoServer.exe..."
& (Join-Path $PSScriptRoot "build.ps1")
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED - not starting." -ForegroundColor Red; exit 1 }

# 2) Trap decks. front = hero card ids front-loaded into the opening hand (opening hand = 5).
#    Card ids: Reflector 174, Statistician 212, Mastermind 224, Pyromancer 86, Healer 64,
#              Assassin 58, Knight 70.
$decks = @(
    @{ name = "1 Cancel Attack (solo)";  front = @(174) }                   # Reflector order: arm Cancel Attack, then let the bot attack into it -> negated + Backlash. SOLO-testable.
    @{ name = "2 Cancel Spell (2p)";     front = @(212,86,64) }             # Statistician(arm) + Pyromancer/Healer wave-spells. Needs a 2nd human to CAST the spell.
    @{ name = "3 Cancel Order (2p)";     front = @(224,58,70) }             # Mastermind(arm) + Assassin/Knight orders. Needs a 2nd human to PLAY the order.
    @{ name = "4 All Traps (2p mirror)"; front = @(174,212,224,86,58) }     # all 3 trap orders + a spell (Pyromancer) + an order (Assassin). Best in a human-vs-human mirror.
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
$lines = (Build-DeckLines $decks) -join "`n"
Set-Content ".\data\traps.decks"  -Value $lines -Encoding utf8   # player A
Set-Content ".\data\traps2.decks" -Value $lines -Encoding utf8   # player B (2-human mirror)

# Bot: a plain finished-hero deck (bodies to attack / be attacked). The bot never casts/orders, so it
# only ever trips Cancel Attack.
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
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338 (fresh build). Accounts 'traps' + 'traps2' seeded." -ForegroundColor Green }
else { Write-Host "Server did not start - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host "SOLO (vs bot): log in as 'traps', pick '1 Cancel Attack (solo)'. Round 2+, play Reflector's"
Write-Host "  ORDER to arm it, then end turn; when the bot attacks, it's negated and the attacker is"
Write-Host "  Backlashed. (Cancel Spell/Order won't fire vs the bot.)"
Write-Host ""
Write-Host "2 HUMANS: A logs in as 'traps', B as 'traps2'; both pick '4 All Traps (2p mirror)' and QUEUE"
Write-Host "  within ~2s of each other (else they get the bot). Arm a trap on your turn; it fires on the"
Write-Host "  other player's next attack / wave-spell / order."
Write-Host ""
Write-Host "Watch:  Get-Content .\server_live.log -Wait   (look for 'TRAP ARMED' and 'TRAP:' lines)"
