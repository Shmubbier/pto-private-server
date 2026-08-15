# Batrov + Pendros diagnostic launcher. Account "batpen" gets 2 decks:
#   Deck 1 = Batrov  (leader card 216 = REAL 108): "move all damage from one hero to another"
#   Deck 2 = Pendros (leader card 200 = REAL 100): "mark a hero as [Vanguard/Flank/Rear]"
# Both need the client to send extra targeting data the server can't yet see. Cast each ONCE, then read
# the diagnostic the server logs so we can finalize the client patch:
#   Get-Content .\server_live.log | Select-String 'LEADER-SPECIAL DIAG'
# That line shows payloadLen + raw hex of exactly what the client sends. Send it back.
Set-Location $PSScriptRoot

$holder = (Get-NetTCPConnection -LocalPort 51338 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
if ($holder) { Write-Host "Killing old server on port 51338 (PID $holder)..."; Stop-Process -Id $holder -Force -ErrorAction SilentlyContinue }
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Write-Host "Rebuilding PtoServer.exe..."
& (Join-Path $PSScriptRoot "build.ps1")
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED - not starting." -ForegroundColor Red; exit 1 }

# leader card id = REAL * 2. Batrov REAL 108 -> 216; Pendros REAL 100 -> 200.
$acct = @(
    @{ name = "1 Batrov (move all damage A->B)"; id = 216 }
    @{ name = "2 Pendros (mark V/F/R)";          id = 200 }
)
# Hero bodies: units to damage / move damage between / mark. Mix of stat + spell heroes.
$filler = @(56,58,60,62,64,68,70,72,78,84,86,88,92,94,96,98,100)
$heroes = @(); while ($heroes.Count -lt 30) { $heroes += $filler }
$heroes = $heroes[0..29]
$heroCsv = $heroes -join ','

$decks = for ($i = 0; $i -lt $acct.Count; $i++) { "$i|0|1|2|$($acct[$i].id),$heroCsv|$($acct[$i].name)" }

New-Item -ItemType Directory -Force -Path ".\data" | Out-Null
Set-Content ".\data\batpen.decks" -Value ($decks -join "`n") -Encoding utf8

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
if ($listening -ge 1) { Write-Host "SERVER RUNNING on port 51338 (fresh build). Account 'batpen' seeded (Batrov + Pendros)." -ForegroundColor Green }
else { Write-Host "Server did not start - check server_live.log" -ForegroundColor Red }
Write-Host ""
Write-Host "Log in as 'batpen' (any password). Deck 1 = Batrov, Deck 2 = Pendros."
Write-Host "Summon a couple of heroes, damage one, then cast the leader ability and target as prompted."
Write-Host "Then grab the diagnostic:  Get-Content .\server_live.log | Select-String 'LEADER-SPECIAL DIAG'"
