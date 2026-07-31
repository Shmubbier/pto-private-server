# Persistent dummy opponent for solo testing. Connects, logs in, queues, and
# auto-completes its side of the battle handshake (op20 ready, op37 mulligan) so a
# human client that queues gets matched and can reach the board / turn 1.
# It does NOT play a turn (no AI yet) — it exists to validate board render + mulligan.
param([string]$Name = "TestBot", [switch]$AutoPlay)
function New-Packet([byte]$op,[byte[]]$pl){ $t=7+$pl.Length; $ms=New-Object System.IO.MemoryStream; $bw=New-Object System.IO.BinaryWriter($ms); $bw.Write([byte]$op); $bw.Write([uint16]1374); $bw.Write([uint32]$t); if($pl.Length){$bw.Write($pl)}; $bw.Flush(); return $ms.ToArray() }
function Str([string]$s){ return [System.Text.Encoding]::UTF8.GetBytes($s)+[byte]0 }
function U16($v){ return [System.BitConverter]::GetBytes([uint16]$v) }

$c=New-Object System.Net.Sockets.TcpClient; $c.Connect("127.0.0.1",51338); $ns=$c.GetStream(); $c.NoDelay=$true
$ns.Write((New-Packet 46 (@([byte]0)+(Str $Name)+(Str "pw")+(U16 72))),0,999) 2>$null
$p=New-Packet 46 (@([byte]0)+(Str $Name)+(Str "pw")+(U16 72)); $ns.Write($p,0,$p.Length); $ns.Flush()
Start-Sleep -Milliseconds 500; $b=New-Object byte[] 16384; [void]$ns.Read($b,0,$b.Length)
$cards=@(2)+(26..55); $dp=@([byte]0)+(Str "Deck")+@([byte]0)+(U16 1)+(U16 2); foreach($cc in $cards){$dp+=(U16 $cc)}
$p=New-Packet 47 $dp; $ns.Write($p,0,$p.Length); $ns.Flush(); Start-Sleep -Milliseconds 300
$p=New-Packet 0 @([byte]0); $ns.Write($p,0,$p.Length); $ns.Flush()
Write-Host "$Name queued, waiting for a human to match..."

$sawStart=$false; $sawBoard=$false; $lastEnd=(Get-Date).AddSeconds(-10); $summoned=$false
$deadline=(Get-Date).AddMinutes(30)
while((Get-Date) -lt $deadline){
  if($ns.DataAvailable){
    $n=$ns.Read($b,0,$b.Length); $i=0
    while($i -le $n-7){
      $op=$b[$i]; $len=[System.BitConverter]::ToUInt32($b,$i+3); if($len -lt 7 -or ($i+$len)-gt $n){break}
      if($op -eq 2 -and -not $sawStart){ $sawStart=$true; Start-Sleep -Milliseconds 300; $q=New-Packet 20 @(); $ns.Write($q,0,$q.Length); $ns.Flush(); Write-Host "matched -> sent op20" }
      elseif($op -eq 4 -and -not $sawBoard){ $sawBoard=$true; Start-Sleep -Milliseconds 300; $q=New-Packet 37 @([byte]0,0,0,0); $ns.Write($q,0,$q.Length); $ns.Flush(); Write-Host "got board -> mulligan kept" }
      elseif($op -eq 14 -and $len -ge 10){
        $player=[System.BitConverter]::ToUInt16($b,$i+7)
        $show_msg=[System.BitConverter]::ToBoolean($b,$i+9)
        if(($AutoPlay -or $player -eq 0) -and $show_msg){
          Start-Sleep -Milliseconds 500
          if(-not $summoned){
            $summoned=$true
            foreach($yy in 0,1,2){ $q=New-Packet 10 @([byte]0,2,[byte]$yy,0); $ns.Write($q,0,$q.Length); $ns.Flush(); Start-Sleep -Milliseconds 400 }
            Write-Host "my turn -> summoned a wave"
            Start-Sleep -Milliseconds 400
          }
          $q=New-Packet 14 @(); $ns.Write($q,0,$q.Length); $ns.Flush(); Write-Host "end turn"
        }
      }
      $i+=$len
    }
  } else { Start-Sleep -Milliseconds 150 }
}
$c.Close()
