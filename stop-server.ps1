# Stops the PTO private server and the test-bot opponent.
Set-Location $PSScriptRoot
Get-Process PtoServer -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like '*testbot*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Write-Host "Server and bot stopped." -ForegroundColor Yellow
