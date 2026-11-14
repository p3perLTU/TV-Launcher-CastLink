# Builds the screen-cast apps with the Android SDK build-tools only (no Gradle), like ..\build.ps1.
#   .\build.ps1 -App receiver   -> cast\receiver\build\receiver.apk   (TV)
#   .\build.ps1 -App sender     -> cast\sender\build\sender.apk       (phone)
# Signed with the same release key as the launcher ($HOME\.tvlauncher).
param([Parameter(Mandatory = $true)][ValidateSet("receiver", "sender")][string]$App)

$ErrorActionPreference = "Continue"
$Root = Join-Path $PSScriptRoot $App
$Common = Join-Path $PSScriptRoot "common\src"
# the shared CastLink look (Android code, so not in common: the Java tests compile common without android.jar)
$UiSrc = Join-Path $PSScriptRoot "ui\src"
$Sdk  = "C:\Android"
$BT   = "$Sdk\build-tools\34.0.0"
$Jar  = "$Sdk\platforms\android-34\android.jar"
$B    = Join-Path $Root "build"
$Out  = "$App.apk"
$MinSdk = if ($App -ne "sender") { 30 } else { 29 }

function Fail($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

foreach ($f in @($Jar, "$BT\aapt2.exe", "$BT\d8.bat", "$BT\apksigner.bat", "$BT\zipalign.exe")) {
    if (-not (Test-Path $f)) { Fail "missing $f" }
}
if (-not (Get-Command javac -ErrorAction SilentlyContinue)) { Fail "javac not found (JDK 17)" }

if (Test-Path $B) { Remove-Item $B -Recurse -Force }
New-Item -ItemType Directory -Path "$B\classes", "$B\dex" | Out-Null

Write-Host "1/5 aapt2 compile + link"
& "$BT\aapt2.exe" compile --dir "$Root\res" -o "$B\res.zip"
if ($LASTEXITCODE -ne 0) { Fail "aapt2 compile failed" }
# The phone app also carries the TV app's receiver, so the phone can show the PC's screen: the
# receiver's resources go in as an overlay (shared names such as app_icon are the same files) and
# its R class is generated for its own package too (--extra-packages).
$extra = @(); $extraSrc = @()
if ($App -eq "sender") {
    $RecvRoot = Join-Path $PSScriptRoot "receiver"
    & "$BT\aapt2.exe" compile --dir "$RecvRoot\res" -o "$B\res-receiver.zip"
    if ($LASTEXITCODE -ne 0) { Fail "aapt2 compile (receiver resources) failed" }
    $extra = @("-R", "$B\res-receiver.zip", "--auto-add-overlay", "--extra-packages", "lt.tvcast.receiver")
    $extraSrc = @("$RecvRoot\src")
}
& "$BT\aapt2.exe" link -o "$B\base.apk" -I $Jar --manifest "$Root\AndroidManifest.xml" --java "$B\gen" --min-sdk-version $MinSdk --target-sdk-version 34 "$B\res.zip" $extra
if ($LASTEXITCODE -ne 0) { Fail "aapt2 link failed" }

Write-Host "2/5 javac"
$src = @("$Root\src", $Common, $UiSrc, "$B\gen") + $extraSrc | ForEach-Object { Get-ChildItem $_ -Recurse -Filter *.java } | ForEach-Object { $_.FullName }
& javac --release 8 -encoding UTF-8 -Xlint:-options -classpath $Jar -d "$B\classes" $src
if ($LASTEXITCODE -ne 0) { Fail "javac failed (see the errors above)" }

Write-Host "3/5 d8"
$classes = Get-ChildItem "$B\classes" -Recurse -Filter *.class | ForEach-Object { $_.FullName }
& "$BT\d8.bat" --release --min-api $MinSdk --lib $Jar --output "$B\dex" $classes
if ($LASTEXITCODE -ne 0) { Fail "d8 failed" }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open("$B\base.apk", [System.IO.Compression.ZipArchiveMode]::Update)
[void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, "$B\dex\classes.dex", "classes.dex")
$zip.Dispose()

Write-Host "4/5 zipalign"
& "$BT\zipalign.exe" -f -p 4 "$B\base.apk" "$B\aligned.apk"
if ($LASTEXITCODE -ne 0) { Fail "zipalign failed" }

Write-Host "5/5 signing"
# same key and DPAPI-protected password as ..\build.ps1; run that once first if the key is missing
$KeyDir   = Join-Path $HOME ".tvlauncher"
$KS       = Join-Path $KeyDir "release.jks"
$PassFile = Join-Path $KeyDir "release-pass.dpapi"
if (-not (Test-Path $KS) -or -not (Test-Path $PassFile)) { Fail "no key $KS (run ..\build.ps1 first)" }
$sec = Get-Content $PassFile | ConvertTo-SecureString
$env:TVL_KS_PASS = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
& "$BT\apksigner.bat" sign --ks $KS --ks-key-alias tvlauncher --ks-pass env:TVL_KS_PASS --out "$B\$Out" "$B\aligned.apk"
$signed = $LASTEXITCODE
Remove-Item Env:TVL_KS_PASS
if ($signed -ne 0) { Fail "apksigner failed" }

Write-Host ""
Write-Host "Built: $B\$Out" -ForegroundColor Green
