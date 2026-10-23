# Builds G3Tuner.exe with the C# compiler that ships with Windows (.NET Framework 4.x)
#   powershell -ExecutionPolicy Bypass -File .\build.ps1          build only
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 -Sign    build, then sign with sign.ps1
param([switch]$Sign)
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
Set-Location $PSScriptRoot
& $csc /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /out:G3Tuner.exe `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll `
    G3Tuner.cs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built G3Tuner.exe"
if ($Sign) { & (Join-Path $PSScriptRoot "sign.ps1"); exit $LASTEXITCODE }
