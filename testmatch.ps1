param([string]$Name1="BotA",[string]$Name2="BotB")
function Pkt($op,$pl){ $t=7+$pl.Length; $ms=New-Object System.IO.MemoryStream; $bw=New-Object System.IO.BinaryWriter($ms); $bw.Write([byte]$op); $bw.Write([uint16]1374); $bw.Write([uint32]$t); if($pl.Length){$bw.Write($pl)}; $bw.Flush(); return $ms.ToArray() }
function S($s){[System.Text.Encoding]::UTF8.GetBytes($s)+[byte]0}
function U16($v){[System.BitConverter]::GetBytes([uint16]$v)}
function LoginDeckQueue($name){
  $c=New-Object System.Net.Sockets.TcpClient; $c.Connect("127.0.0.1",51338); $ns=$c.GetStream(); $c.NoDelay=$true
  $p=Pkt 46 (@([byte]0)+(S $name)+(S "pw")+(U16 72)); $ns.Write($p,0,$p.Length); Start-Sleep -Milliseconds 500
  $b=New-Object byte[] 16384; try{[void]$ns.Read($b,0,$b.Length)}catch{}; Start-Sleep -Milliseconds 200
  $cards=@(2)+(26..55); $dp=@([byte]0)+(S $name)+@([byte]0)+(U16 1)+(U16 2); foreach($cc in $cards){$dp+=(U16 $cc)}
  $ns.Write((Pkt 47 $dp),0,999) 2>$null; Start-Sleep -Milliseconds 300
  $ns.Write((Pkt 0 @([byte]0)),0,999) 2>$null; Start-Sleep -Milliseconds 100
  Write-Host "$name queued"; return @{ns=$ns; buf=[Collections.ArrayList]@(); name=$name; summoned=$false; round2=$false; c=$c}
}
function ReadAny($ctx,$timeoutMs){
  $dl=[DateTime]::UtcNow.AddMilliseconds($timeoutMs)
  while([DateTime]::UtcNow -lt $dl){
    if($ctx.ns.DataAvailable){
      $chunk=New-Object byte[] 4096; $n=$ctx.ns.Read($chunk,0,$chunk.Length); if($n -gt 0){[void]$ctx.buf.AddRange($chunk[0..($n-1)])}
    }
    if($ctx.buf.Count -ge 7){
      $len=[System.BitConverter]::ToUInt32($ctx.buf,3)
      if($len -ge 7 -and $ctx.buf.Count -ge $len){
        $pkt=@($ctx.buf[0..([int]$len-1)]); $ctx.buf.RemoveRange(0,$len); return $pkt
      }
    }
    Start-Sleep -Milliseconds 20
  }
  return $null
}
function Drain($ctxs,$ms){ $dl=[DateTime]::UtcNow.AddMilliseconds($ms); while([DateTime]::UtcNow -lt $dl){ foreach($ctx in $ctxs){ ReadAny $ctx 50 | Out-Null } }; Start-Sleep -Milliseconds 20 }

Write-Host "=== Connecting $Name1 and $Name2 ==="
$p0=LoginDeckQueue $Name1; $p1=LoginDeckQueue $Name2; Start-Sleep -Milliseconds 1000
Drain @($p0,$p1) 500

# Wait for match — both get op2
Write-Host "--- Waiting for match ---"
$matched=$false
$dl=[DateTime]::UtcNow.AddSeconds(30)
while(-not $matched -and [DateTime]::UtcNow -lt $dl){
  foreach($ctx in @($p0,$p1)){
    $pkt=ReadAny $ctx 100
    if($pkt -and $pkt[0] -eq 2){ Write-Host "$($ctx.name) matched"; $ctx.matched=$true }
  }
  $matched=($p0.matched -and $p1.matched)
}
if(-not $matched){ Write-Host "Match failed"; return }

# Both send op20 ready
foreach($ctx in @($p0,$p1)){ $ctx.ns.Write((Pkt 20 @()),0,999) 2>$null; Write-Host "$($ctx.name) ready"; Start-Sleep -Milliseconds 200 }
Drain @($p0,$p1) 1000

# Mulligan
foreach($ctx in @($p0,$p1)){ $ctx.ns.Write((Pkt 37 @([byte]0,0,0,0)),0,999) 2>$null; Write-Host "$($ctx.name) mulligan"; Start-Sleep -Milliseconds 300 }
Drain @($p0,$p1) 1000

# Main game loop
Write-Host "=== Battle started, playing turns ==="
$turnCount=0; $dl=[DateTime]::UtcNow.AddSeconds(120); $attackAttempted=$false
while([DateTime]::UtcNow -lt $dl){
  foreach($ctx in @($p0,$p1)){
    $pkt=ReadAny $ctx 50
    if(-not $pkt){ continue }
    $op=$pkt[0]
    # Track round 2: op 24 = BattleAttackPhase
    if($op -eq 24){ Write-Host "$($ctx.name) got BattleAttackPhase (round 2)"; $ctx.round2=$true; $p0.round2=$true; $p1.round2=$true }
    # TurnGet
    if($op -eq 14 -and $pkt.Length -ge 10){
      $player=[System.BitConverter]::ToUInt16($pkt,7)
      $show=[System.BitConverter]::ToBoolean($pkt,9)
      if($show){
        $turnCount++
        Write-Host "$($ctx.name) turn (player=$player, wave turn #$turnCount, round2=$($ctx.round2))"
        Start-Sleep -Milliseconds 300
        # Summon on first turn in wave 2
        if(-not $ctx.summoned){
          $ctx.summoned=$true
          foreach($yy in 0,1,2){ $ctx.ns.Write((Pkt 10 @([byte]0,2,[byte]$yy,0)),0,999) 2>$null; Start-Sleep -Milliseconds 300 }
          Write-Host "$($ctx.name) summoned wave 2"
          Start-Sleep -Milliseconds 300
        }
        # Attack with leader in round 2
        if($ctx.round2 -and -not $attackAttempted){
          $attackAttempted=$true
          Write-Host "$($ctx.name) ATTEMPTING ATTACK: leader(1,1) -> (2,0)"
          $ctx.ns.Write((Pkt 22 @([byte]0,1,1,2,0)),0,999) 2>$null
          Start-Sleep -Milliseconds 500
        }
        # End turn
        $ctx.ns.Write((Pkt 14 @()),0,999) 2>$null
        Write-Host "$($ctx.name) end turn"
        Start-Sleep -Milliseconds 200
      }
    }
  }
  Start-Sleep -Milliseconds 50
}
Write-Host "=== Test match complete ==="
$p0.c.Close(); $p1.c.Close()
