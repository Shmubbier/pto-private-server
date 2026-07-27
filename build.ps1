# Compiles PtoServer.cs -> PtoServer.exe and PtoHarness.cs -> PtoHarness.exe
# using the in-box .NET Framework C# compiler. No .NET SDK or internet required.
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /optimize+ /out:(Join-Path $PSScriptRoot "PtoServer.exe")  (Join-Path $PSScriptRoot "PtoServer.cs")
if ($LASTEXITCODE -ne 0) { Write-Host "server build failed"; exit 1 }
& $csc /nologo /optimize+ /out:(Join-Path $PSScriptRoot "PtoHarness.exe") (Join-Path $PSScriptRoot "PtoHarness.cs")
if ($LASTEXITCODE -ne 0) { Write-Host "harness build failed"; exit 1 }
Write-Host "OK -> PtoServer.exe, PtoHarness.exe"
