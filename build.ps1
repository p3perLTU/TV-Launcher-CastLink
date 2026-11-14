# Builds tvlauncher.apk with the Android SDK build-tools only (no Gradle).
# Needs: JDK 17 (javac, keytool on PATH) and C:\Android with platforms;android-34 + build-tools;34.0.0
param([switch]$ShowPassword)

$ErrorActionPreference = "Continue"
$Root = $PSScriptRoot
$Sdk  = "C:\Android"
$BT   = "$Sdk\build-tools\34.0.0"
$Jar  = "$Sdk\platforms\android-34\android.jar"
$B    = Join-Path $Root "build"

function Fail($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

foreach ($f in @($Jar, "$BT\aapt2.exe", "$BT\d8.bat", "$BT\apksigner.bat", "$BT\zipalign.exe")) {
    if (-not (Test-Path $f)) { Fail "missing $f" }
}
if (-not (Get-Command javac -ErrorAction SilentlyContinue))   { Fail "javac not found (JDK 17)" }
if (-not (Get-Command keytool -ErrorAction SilentlyContinue)) { Fail "keytool not found (JDK 17)" }

if (Test-Path $B) { Remove-Item $B -Recurse -Force }
New-Item -ItemType Directory -Path "$B\classes", "$B\dex" | Out-Null

Write-Host "1/6 aapt2 compile + link"
& "$BT\aapt2.exe" compile --dir "$Root\res" -o "$B\res.zip"
if ($LASTEXITCODE -ne 0) { Fail "aapt2 compile failed" }
& "$BT\aapt2.exe" link -o "$B\base.apk" -I $Jar --manifest "$Root\AndroidManifest.xml" --java "$B\gen" --min-sdk-version 26 --target-sdk-version 34 "$B\res.zip"
if ($LASTEXITCODE -ne 0) { Fail "aapt2 link failed" }

Write-Host "2/6 javac"
$src = @("$Root\src", "$B\gen") | ForEach-Object { Get-ChildItem $_ -Recurse -Filter *.java } | ForEach-Object { $_.FullName }
& javac --release 8 -encoding UTF-8 -Xlint:-options -classpath $Jar -d "$B\classes" $src
if ($LASTEXITCODE -ne 0) { Fail "javac failed (see the errors above)" }

Write-Host "3/6 d8"
$classes = Get-ChildItem "$B\classes" -Recurse -Filter *.class | ForEach-Object { $_.FullName }
& "$BT\d8.bat" --release --min-api 26 --lib $Jar --output "$B\dex" $classes
if ($LASTEXITCODE -ne 0) { Fail "d8 failed" }

Write-Host "4/6 classes.dex + assets -> apk"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open("$B\base.apk", [System.IO.Compression.ZipArchiveMode]::Update)
[void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, "$B\dex\classes.dex", "classes.dex")
# assets are added here, not with aapt2 -A: aapt2 on Windows writes backslashes into entry names
$A = Join-Path $Root "assets"
if (Test-Path $A) {
    Get-ChildItem $A -Recurse -File | ForEach-Object {
        $rel = "assets/" + $_.FullName.Substring($A.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $rel)
    }
}
$zip.Dispose()

Write-Host "5/6 zipalign"
& "$BT\zipalign.exe" -f -p 4 "$B\base.apk" "$B\aligned.apk"
if ($LASTEXITCODE -ne 0) { Fail "zipalign failed" }

Write-Host "6/6 signing"
# Release key outside the project folder. Its random password is stored encrypted with
# Windows DPAPI (readable only by this Windows user on this PC) and handed to keytool and
# apksigner through an environment variable, never on the command line.
# Show the password (to keep it in a password manager):  .\build.ps1 -ShowPassword
$KeyDir  = Join-Path $HOME ".tvlauncher"
$KS      = Join-Path $KeyDir "release.jks"
$PassFile = Join-Path $KeyDir "release-pass.dpapi"
if (-not (Test-Path $KS)) {
    New-Item -ItemType Directory -Force $KeyDir | Out-Null
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $plain = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', 'A').Replace('/', 'B')
    ConvertTo-SecureString $plain -AsPlainText -Force | ConvertFrom-SecureString | Set-Content $PassFile -Encoding ascii
    $env:TVL_KS_PASS = $plain
    & keytool -genkeypair -keystore $KS -storetype PKCS12 -storepass:env TVL_KS_PASS -alias tvlauncher -keyalg RSA -keysize 3072 -validity 10000 -dname "CN=TV Launcher"
    if ($LASTEXITCODE -ne 0) { Remove-Item Env:TVL_KS_PASS; Fail "keytool failed" }
    Write-Host "   New key created: $KS" -ForegroundColor Yellow
}
if (-not (Test-Path $PassFile)) { Fail "missing $PassFile (the key password)" }
$sec = Get-Content $PassFile | ConvertTo-SecureString
$env:TVL_KS_PASS = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
if ($ShowPassword) { Write-Host "Key password: $env:TVL_KS_PASS" -ForegroundColor Yellow }
& "$BT\apksigner.bat" sign --ks $KS --ks-key-alias tvlauncher --ks-pass env:TVL_KS_PASS --out "$B\tvlauncher.apk" "$B\aligned.apk"
$signed = $LASTEXITCODE
Remove-Item Env:TVL_KS_PASS
if ($signed -ne 0) { Fail "apksigner failed" }

Write-Host ""
Write-Host "Built: $B\tvlauncher.apk" -ForegroundColor Green

