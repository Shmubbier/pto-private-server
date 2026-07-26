# Compiles PtoServer.cs into PtoServer.exe using the in-box .NET Framework C# compiler.
# No .NET SDK or internet required.
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = Join-Path $PSScriptRoot "PtoServer.cs"
$out = Join-Path $PSScriptRoot "PtoServer.exe"
Write-Host "Compiling $src ..."
& $csc /nologo /optimize+ /out:$out $src
if ($LASTEXITCODE -eq 0) { Write-Host "OK -> $out" } else { Write-Host "Build failed ($LASTEXITCODE)" }
