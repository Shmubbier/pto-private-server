# Compiles PtoServer.cs -> PtoServer.exe using the in-box .NET Framework C# compiler.
# No .NET SDK or internet required.
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# Build each into single, quoted args. NOTE: /out:(Join-Path ...) does NOT work — PowerShell splits
# it into a bare "/out:" plus a separate path token, so csc gets an empty /out: and no source file.
# Interpolate the resolved path into one quoted string instead.
$srvExe = Join-Path $PSScriptRoot "PtoServer.exe"
$srvSrc = Join-Path $PSScriptRoot "PtoServer.cs"
& $csc /nologo /optimize+ "/out:$srvExe" "$srvSrc"
if ($LASTEXITCODE -ne 0) { Write-Host "server build failed"; exit 1 }

Write-Host "OK -> PtoServer.exe"
