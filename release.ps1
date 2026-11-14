# Tests and builds all four apps, then publishes them as a GitHub release of this
# repository.
#   powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 1.1 -Notes "What's new"
#   powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 1.1 -DryRun   (everything but the upload)
# Before: raise versionCode (and versionName) in the AndroidManifest.xml of every app that
# changed, commit and push. The script refuses to run on uncommitted or unpushed code or
# failing tests, and refuses to upload anything that contains a LAN address, a key or one
# of the personal words listed in %USERPROFILE%\.tvlauncher\release-check-words.txt.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [switch]$DryRun
)

$ErrorActionPreference = "Continue"
$env:Path = "C:\Program Files\Git\cmd;C:\Program Files\GitHub CLI;" + $env:Path
$Root = $PSScriptRoot
$Dist = Join-Path $Root "dist"
$Tag = "v$Version"
$BT = "C:\Android\build-tools\34.0.0"

function Fail($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }
function Step($msg) { Write-Host ""; Write-Host "== $msg" -ForegroundColor Cyan }

foreach ($tool in "git", "gh") { if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Fail "$tool not found" } }
Set-Location $Root

Step "Source"
if (-not ($Version -match '^\d+\.\d+(\.\d+)?$')) { Fail "the version must look like 1.1 or 1.1.2" }
$dirty = git status --porcelain
if ($dirty) { Fail "uncommitted changes (git commit):`n$($dirty -join "`n")" }
git fetch -q origin
if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) { Fail "the code is not pushed to GitHub (git push)" }
gh release view $Tag 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { Fail "release $Tag already exists" }
Write-Host "   OK  everything committed and pushed, $Tag does not exist yet"

Step "Tests"
& powershell -ExecutionPolicy Bypass -File (Join-Path $Root "tests\run-tests.ps1") | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { Fail "tests failed (run tests\run-tests.ps1 to see which)" }

function ManifestVersion($path) {
    $x = [xml](Get-Content $path -Raw -Encoding UTF8)
    $a = "http://schemas.android.com/apk/res/android"
    return @{ Package = $x.manifest.package; Name = $x.manifest.GetAttribute("versionName", $a); Code = $x.manifest.GetAttribute("versionCode", $a) }
}
$apps = @(
    @{ Title = "Launcher (TV)";             Manifest = "AndroidManifest.xml";               Build = "build\tvlauncher.apk";            Out = "TVLauncher" },
    @{ Title = "Cast receiver (TV)";        Manifest = "cast\receiver\AndroidManifest.xml"; Build = "cast\receiver\build\receiver.apk"; Out = "CastLink-TV" },
    @{ Title = "Cast sender (phone)";       Manifest = "cast\sender\AndroidManifest.xml";   Build = "cast\sender\build\sender.apk";     Out = "CastLink-Phone" }
)
foreach ($a in $apps) { $a.Version = ManifestVersion (Join-Path $Root $a.Manifest); $a.File = "$($a.Out)-$($a.Version.Name).apk" }

Step "Building"
& powershell -ExecutionPolicy Bypass -File (Join-Path $Root "build.ps1") | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { Fail "launcher build failed" }
foreach ($app in "receiver", "sender") {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $Root "cast\build.ps1") -App $app | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { Fail "$app build failed" }
}
& powershell -ExecutionPolicy Bypass -File (Join-Path $Root "cast\pc\build.ps1") | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { Fail "PC app build failed" }

Step "Copying to dist"
# a running dist\CastLink.exe holds its exe and DLLs open
if (Get-CimInstance Win32_Process -Filter "Name='CastLink.exe'" | Where-Object { $_.ExecutablePath -like "$Dist\*" }) { Fail "close dist\CastLink.exe first (it keeps its files open)" }
foreach ($a in $apps) {
    Get-ChildItem $Dist -Filter "$($a.Out)-*.apk" | Remove-Item
    Copy-Item (Join-Path $Root $a.Build) (Join-Path $Dist $a.File)
    Write-Host "   $($a.File)"
}
Copy-Item (Join-Path $Root "cast\pc\build\CastLink.exe") (Join-Path $Dist "CastLink.exe") -Force
# the PC app needs its lib folder (WebRTC libraries + their licences): shipped together as a zip
$PcLib = Join-Path $Root "cast\pc\build\lib"
if (-not (Test-Path (Join-Path $PcLib "THIRD-PARTY-NOTICES.md"))) { Fail "cast\pc\build\lib has no THIRD-PARTY-NOTICES.md (run cast\pc\restore-packages.ps1)" }
# dist\lib = a mirror of build\lib (robocopy /MIR: also removes files that are gone)
robocopy $PcLib (Join-Path $Dist "lib") /MIR /NJH /NJS /NP /NFL /NDL | Out-Null
if ($LASTEXITCODE -ge 8) { Fail "copying lib to dist failed" }
$PcZip = Join-Path $Dist "CastLink-PC-$Version.zip"
Get-ChildItem $Dist -Filter "CastLink-PC-*.zip" | Remove-Item
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::Open($PcZip, "Create")
try {
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, (Join-Path $Dist "CastLink.exe"), "CastLink.exe")
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, (Join-Path $Root "LICENSE"), "LICENSE")
    Get-ChildItem $PcLib -Recurse -File | ForEach-Object {
        $rel = "lib/" + $_.FullName.Substring($PcLib.Length + 1).Replace("\", "/")
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $_.FullName, $rel)
    }
} finally { $z.Dispose() }
Write-Host "   CastLink-PC-$Version.zip (CastLink.exe, LICENSE, lib\: $((Get-ChildItem $PcLib -Recurse -File).Count) files)"
$upload = @($apps | ForEach-Object { Join-Path $Dist $_.File }) + $PcZip, (Join-Path $Dist "INSTALL.md")

Step "Checking for personal data and keys"
# private network addresses, e-mails, keys; plus personal words (names, Wi-Fi names, device
# serials) from a local list outside the repo, one per line, so they never end up in git
$bad = '(?i)@gmail|\b192\.168\.\d{1,3}\.\d{1,3}\b|\b10\.\d{1,3}\.\d{1,3}\.\d{1,3}\b|\b172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}\b|-----BEGIN|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35}|gh[pousr]_[0-9A-Za-z]{30,}'
$words = Join-Path $env:USERPROFILE ".tvlauncher\release-check-words.txt"
if (Test-Path $words) {
    Get-Content $words -Encoding UTF8 | Where-Object { $_.Trim() } | ForEach-Object { $bad += "|" + [regex]::Escape($_.Trim()) }
} else { Write-Host "   (no $words - only addresses and keys are checked)" -ForegroundColor Yellow }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$found = 0
foreach ($f in $upload) {
    $parts = @()
    if ($f -like "*.apk" -or $f -like "*.zip") {
        $z = [IO.Compression.ZipFile]::OpenRead($f)
        foreach ($e in $z.Entries) {
            $s = $e.Open(); $m = New-Object IO.MemoryStream; $s.CopyTo($m); $s.Close()
            if ($e.FullName -like "lib/*") {
                # third-party files: no personal data can be in them if they are byte-identical to
                # what restore-packages.ps1 took from the hash-checked NuGet packages (their own
                # version numbers and e-mails would only be false alarms)
                $src = Join-Path $Root ("cast\pc\lib\" + $e.FullName.Substring(4).Replace("/", "\"))
                if (-not (Test-Path $src) -or [Convert]::ToBase64String([Security.Cryptography.SHA256]::Create().ComputeHash($m.ToArray())) -ne [Convert]::ToBase64String([Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($src)))) {
                    $found++; Write-Host "   FOUND: $([IO.Path]::GetFileName($f)) $($e.FullName) differs from cast\pc\lib" -ForegroundColor Red
                }
                continue
            }
            $parts += ,@($e.FullName, $m.ToArray())
        }
        $z.Dispose()
    } else { $parts += ,@("", [IO.File]::ReadAllBytes($f)) }
    foreach ($p in $parts) {
        $b = $p[1]
        $text = [Text.Encoding]::ASCII.GetString($b) + [Text.Encoding]::Unicode.GetString($b) + [Text.Encoding]::UTF8.GetString($b)
        $hit = [regex]::Match($text, $bad)
        if ($hit.Success) { $found++; Write-Host "   FOUND: $([IO.Path]::GetFileName($f)) $($p[0]): '$($hit.Value)'" -ForegroundColor Red }
    }
}
foreach ($a in $apps) {
    $dn = (& "$BT\apksigner.bat" verify --print-certs (Join-Path $Dist $a.File) | Select-String "DN:") -replace '^.*DN: ', ''
    if ($dn -ne "CN=TV Launcher") { $found++; Write-Host "   FOUND: $($a.File) is signed by '$dn'" -ForegroundColor Red }
}
if ($found -gt 0) { Fail "personal data or a wrong key in the files - nothing uploaded" }
Write-Host "   OK  nothing found, all apps signed by CN=TV Launcher"

$body = @"
| App | File | Version |
|---|---|---|
$(($apps | ForEach-Object { "| $($_.Title) | ``$($_.File)`` | $($_.Version.Name) (code $($_.Version.Code)) |" }) -join "`n")
| Cast sender (Windows 10/11), also PC -> iPad | ``CastLink-PC-$Version.zip``: unzip, run ``CastLink.exe``, keep the ``lib`` folder next to it | $Version |

$(if ($Notes) { "**What's new:** $Notes`n" })
How to install: **INSTALL.md**. A new version installs over the old one; settings are kept.

The PC zip's ``lib`` folder holds third-party WebRTC libraries (SIPSorcery and its dependencies) under their own licences, listed in ``lib\THIRD-PARTY-NOTICES.md``. SIPSorcery's licence adds a use restriction (not inside Israel and the Occupied Territories).
"@

Step "Release $Tag"
$upload | ForEach-Object { Write-Host "   $([IO.Path]::GetFileName($_))  ($([math]::Round((Get-Item $_).Length / 1KB)) KB)" }
if ($DryRun) {
    Write-Host ""; Write-Host "DRY RUN: nothing uploaded. The release notes would be:" -ForegroundColor Yellow; Write-Host $body
    exit 0
}
$nf = Join-Path $env:TEMP "release-notes-$Tag.md"
Set-Content $nf $body -Encoding UTF8
gh release create $Tag @upload --title "CastLink $Version" --notes-file $nf
$ok = $LASTEXITCODE
Remove-Item $nf
if ($ok -ne 0) { Fail "upload failed" }
Write-Host ""; Write-Host "Uploaded: $(gh release view $Tag --json url --jq .url)" -ForegroundColor Green
