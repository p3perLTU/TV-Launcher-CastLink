# Builds the Windows sender with the C# compiler that comes with Windows (.NET Framework 4.x).
#   .\build.ps1   -> cast\pc\build\CastLink.exe
$ErrorActionPreference = "Continue"
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { Write-Host "ERROR: $csc not found" -ForegroundColor Red; exit 1 }
$B = Join-Path $PSScriptRoot "build"
New-Item -ItemType Directory -Force $B | Out-Null
# WebRTC libraries (iPad over UDP) from NuGet, checked by hash; fetched once
$Lib = Join-Path $PSScriptRoot "lib"
if (-not (Test-Path (Join-Path $Lib "SIPSorcery.dll"))) {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "restore-packages.ps1")
    if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: restore-packages.ps1 failed" -ForegroundColor Red; exit 1 }
}
$refs = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\netstandard.dll", "System.Web.Extensions.dll") +
    @("SIPSorcery.dll", "SIPSorceryMedia.Abstractions.dll", "Concentus.dll", "Microsoft.Extensions.Logging.Abstractions.dll" | ForEach-Object { Join-Path $Lib $_ }) | ForEach-Object { "/r:$_" }
# every .cs in this folder; the iPad page is embedded in the exe (WebCast.Page() reads it)
$src = Get-ChildItem $PSScriptRoot -Filter *.cs | ForEach-Object { $_.FullName }
$page = Join-Path $PSScriptRoot "web\ipad.html"
# the icon is drawn in code (AppIcon.cs): a small tool writes build\app.ico first
& $csc /nologo /optimize+ /target:exe /define:ICONTOOL "/out:$B\make-icon.exe" /r:System.Drawing.dll (Join-Path $PSScriptRoot "AppIcon.cs")
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: icon tool failed to compile" -ForegroundColor Red; exit 1 }
& "$B\make-icon.exe" "$B\app.ico"
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: icon tool failed" -ForegroundColor Red; exit 1 }
& $csc /nologo /codepage:65001 /unsafe /optimize+ /platform:x64 /target:winexe `
    "/out:$B\CastLink.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll /r:System.Numerics.dll $refs `
    "/win32icon:$B\app.ico" "/resource:$B\app.ico,TVCast.app.ico" `
    "/resource:$page,TVCast.ipad.html" $src
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: compilation failed" -ForegroundColor Red; exit 1 }
# the exe loads them from lib\ next to itself (AssemblyResolve in Main)
New-Item -ItemType Directory -Force "$B\lib" | Out-Null
Copy-Item "$Lib\*" "$B\lib" -Recurse -Force
Write-Host "Built: $B\CastLink.exe" -ForegroundColor Green
