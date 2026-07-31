# Focused test: two clients, summon one unit then immediately end turn
# Based on working testbot.ps1 logic

function New-Packet([byte]$op, [byte[]]$pl) {
    $t = 7 + $pl.Length
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([byte]$op)
    $bw.Write([uint16]1374)
    $bw.Write([uint32]$t)
    if($pl.Length) { $bw.Write($pl) }
    $bw.Flush()
    return $ms.ToArray()
}
function Str([string]$s) { return [System.Text.Encoding]::UTF8.GetBytes($s) + [byte]0 }
function U16($v) { return [System.BitConverter]::GetBytes([uint16]$v) }

Write-Host "Starting test match..."

# Connect both clients
$c0 = New-Object System.Net.Sockets.TcpClient; $c0.Connect("127.0.0.1", 51338); $ns0 = $c0.GetStream(); $c0.NoDelay=$true
$c1 = New-Object System.Net.Sockets.TcpClient; $c1.Connect("127.0.0.1", 51338); $ns1 = $c1.GetStream(); $c1.NoDelay=$true

function Login($ns, $name) {
    $p = New-Packet 46 (@([byte]0) + (Str $name) + (Str "pw") + (U16 72))
    $ns.Write($p, 0, $p.Length); $ns.Flush()
    Start-Sleep -Milliseconds 300
    $b = New-Object byte[] 16384; $ns.Read($b, 0, $b.Length) | Out-Null; Start-Sleep -Milliseconds 200
    $cards = @(2)+(26..55); $dp = @([byte]0)+(Str "Deck")+@([byte]0)+(U16 1)+(U16 2); foreach($cc in $cards){$dp+=(U16 $cc)}
    $p2 = New-Packet 47 $dp; $ns.Write($p2, 0, $p2.Length); $ns.Flush(); Start-Sleep -Milliseconds 300
    $p3 = New-Packet 0 @([byte]0); $ns.Write($p3, 0, $p3.Length); $ns.Flush(); Start-Sleep -Milliseconds 100
}

Login $ns0 "BotA"
Login $ns1 "BotB"
Write-Host "Both queued"

$b0 = New-Object byte[] 16384
$b1 = New-Object byte[] 16384

$state0 = @{ Name="BotA"; Summoned=$false; TurnCount=0; Wave=2; Round=1 }
$state1 = @{ Name="BotB"; Summoned=$false; TurnCount=0; Wave=2; Round=1 }

function Process($ns, $buf, $state, $myIdx) {
    if ($ns.DataAvailable) {
        try {
            $n = $ns.Read($buf, 0, $buf.Length)
            $i = 0
            while ($i -le $n - 7) {
                $op = $buf[$i]
                $len = [System.BitConverter]::ToUInt32($buf, $i + 3)
                if ($len -lt 7 -or ($i + $len) -gt $n) { break }
                $payload = $buf[$i..($i+$len-1)]

                switch ($op) {
                    2 { # BattleStart
                        if (-not $state.SawStart) {
                            $state.SawStart = $true
                            Start-Sleep -Milliseconds 300
                            $q = New-Packet 20 @()
                            $ns.Write($q, 0, $q.Length); $ns.Flush()
                            Write-Host ("{0} -> op20 Ready" -f $state.Name)
                        }
                    }
                    4 { # BoardInfo
                        if (-not $state.SawBoard) {
                            $state.SawBoard = $true
                            Start-Sleep -Milliseconds 300
                            $q = New-Packet 37 @([byte]0,0,0,0)
                            $ns.Write($q, 0, $q.Length); $ns.Flush()
                            Write-Host ("{0} -> op37 Mulligan keep" -f $state.Name)
                        }
                    }
                    14 { # TurnGet
                        if ($len -ge 10) {
                            $player = [System.BitConverter]::ToUInt16($buf, $i + 7)
                            $show = [System.BitConverter]::ToBoolean($buf, $i + 9)
                            Write-Host ("{0}: RECV TurnGet player={1} show={2}" -f $state.Name, $player, $show)
                            # player field: 0 = you are active, 1 = you are NOT active. show=true for both.
                            if ($show -and $player -eq 0) {
                                $state.TurnCount = $state.TurnCount + 1
                                Write-Host ("{0}: Turn {1} - wave={2} round={3}" -f $state.Name, $state.TurnCount, $state.Wave, $state.Round)
                                Start-Sleep -Milliseconds 300
                                if (-not $state.Summoned) {
                                    Write-Host ("{0}: SUMMONING wave 2, col 0" -f $state.Name)
                                    $q = New-Packet 10 @([byte]0, 2, [byte]0, 0)
                                    $ns.Write($q, 0, $q.Length); $ns.Flush()
                                    $state.Summoned = $true
                                    Start-Sleep -Milliseconds 1000
                                }
                                Write-Host ("{0}: END TURN" -f $state.Name)
                                $q = New-Packet 14 @()
                                $ns.Write($q, 0, $q.Length); $ns.Flush()
                            }
                        }
                    }
                    3 { # WaveUpdate
                        $newWave = $buf[$i+7]
                        $first = [System.BitConverter]::ToUInt16($buf, $i+8)
                        Write-Host ("{0}: WaveUpdate -> wave={1} first={2}" -f $state.Name, $newWave, $first)
                        $state.Wave = $newWave
                        $state.Summoned = $false
                        if ($newWave -eq 2) { $state.Round = $state.Round + 1 }
                    }
                    5 { Write-Host ("{0}: RECV SummonUnit (len={1})" -f $state.Name, $len) }
                    6 { Write-Host ("{0}: RECV SummonUnitGet (len={1})" -f $state.Name, $len) }
                    18 { if ($len -ge 18) { $act = $payload[17]; Write-Host ("{0}: RECV UpdateUnit activate={1} x={2} y={3}" -f $state.Name, $act, $payload[7], $payload[8]) } }
                    19 { if ($len -ge 18) { $act = $payload[17]; Write-Host ("{0}: RECV UpdateUnitGet activate={1} x={2} y={3}" -f $state.Name, $act, $payload[7], $payload[8]) } }
                    25 { Write-Host ("{0}: BATTLE END" -f $state.Name); return $true }
                }
                $i += $len
            }
        } catch { Write-Host ("{0} error: {1}" -f $state.Name, $_); return $true }
    }
    return $false
}

$deadline = (Get-Date).AddMinutes(2)
$done = $false
while (-not $done -and (Get-Date) -lt $deadline) {
    $done = Process $ns0 $b0 $state0 0
    if (-not $done) { $done = Process $ns1 $b1 $state1 1 }
    if (-not $done) { Start-Sleep -Milliseconds 50 }
}
$c0.Close(); $c1.Close()
Write-Host "=== Test complete ==="