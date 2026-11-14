# Runs all unit tests; needs only what the builds need (JDK 17, the C# compiler in Windows).
#   powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
# Exit code 0 when everything passed. release.ps1 runs this before building.
#
#   1. Java  - plain-Java code shared by the apps (protocol, frame assembly, launcher helpers),
#              compiled without android.jar, so it also proves these classes need no Android
#   2. C#    - the PC sender (protocol bytes, NV12 colours, mDNS parsing, settings lookup,
#              iPad mode: fMP4 muxer, HTTP/WebSocket parsing, input messages)
#   3. iPad page - the JavaScript of cast/pc/web/ipad.html in headless Chrome or Edge
#              (tests/web/ipad-tests.js appended to the real page); skipped without a browser
#   4. PowerShell - every script in the repository parses

$ErrorActionPreference = "Continue"
$Tests = $PSScriptRoot
$Root = Split-Path $Tests
$Out = Join-Path $Tests "out"
$failed = @()

function Step($msg) { Write-Host ""; Write-Host "== $msg" -ForegroundColor Cyan }

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force "$Out\java", "$Out\cs" | Out-Null

# ---------------------------------------------------------------- Java
Step "Java"
if (-not (Get-Command javac -ErrorAction SilentlyContinue)) {
    $failed += "Java (javac not found, JDK 17 needed)"
} else {
    # the app classes under test: everything in cast/common, and the launcher's plain-Java helpers
    $pure = @("DockGrid.java", "LogoPixels.java", "WeatherCode.java") | ForEach-Object { Join-Path $Root "src\lt\tvcast\launcher\$_" }
   $src = @(Get-ChildItem (Join-Path $Root "cast\common\src"), (Join-Path $Tests "java") -Recurse -Filter *.java | ForEach-Object { $_.FullName }) + $pure
    & javac --release 8 -encoding UTF-8 -Xlint:-options -d "$Out\java" $src
    if ($LASTEXITCODE -ne 0) {
        $failed += "Java (compile)"
    } else {
        $classes = Get-ChildItem (Join-Path $Tests "java") -Recurse -Filter *Test.java | ForEach-Object {
            $_.FullName.Substring((Join-Path $Tests "java").Length + 1).Replace('\', '.') -replace '\.java$', ''
        }
        & java -cp "$Out\java" lt.tvcast.testing.TestRunner $classes
        if ($LASTEXITCODE -ne 0) { $failed += "Java" }
    }
}

# ---------------------------------------------------------------- C#
Step "C# (PC sender)"
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $failed += "C# ($csc not found)"
} else {
    $exe = Join-Path $Out "cs\TVCast-PC-Tests.exe"
    $pcSrc = Get-ChildItem (Join-Path $Root "cast\pc") -Filter *.cs | ForEach-Object { $_.FullName }
    $lib = Join-Path $Root "cast\pc\lib"
    if (-not (Test-Path (Join-Path $lib "SIPSorcery.dll"))) { & powershell -ExecutionPolicy Bypass -File (Join-Path $Root "cast\pc\restore-packages.ps1") | Out-Null }
    $rtcRefs = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\netstandard.dll", "System.Web.Extensions.dll") +
        @("SIPSorcery.dll", "SIPSorceryMedia.Abstractions.dll", "Concentus.dll", "Microsoft.Extensions.Logging.Abstractions.dll" | ForEach-Object { Join-Path $lib $_ }) | ForEach-Object { "/r:$_" }
    $page = Join-Path $Root "cast\pc\web\ipad.html"
    & $csc /nologo /codepage:65001 /unsafe /platform:x64 /target:exe /main:TVCast.Tests.TestProgram `
        "/out:$exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll /r:System.Numerics.dll $rtcRefs "/resource:$page,TVCast.ipad.html" `
        $pcSrc (Join-Path $Tests "cs\Tests.cs")
    if ($LASTEXITCODE -ne 0) {
        $failed += "C# (compile)"
    } else {
        # the test exe also finds the WebRTC libraries in lib\ next to it
        New-Item -ItemType Directory -Force (Join-Path $Out "cs\lib") | Out-Null
        Copy-Item "$lib\*" (Join-Path $Out "cs\lib") -Recurse -Force
        & $exe (Join-Path $Root "cast\common\src\lt\tvcast\common\Proto.java")
        if ($LASTEXITCODE -ne 0) { $failed += "C#" }
    }
}

# ---------------------------------------------------------------- iPad page (JavaScript)
Step "iPad page (headless Chrome / Edge)"
$browser = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $browser) {
    # not a failure: the page's protocol parts are covered by the C# tests too
    Write-Host "  skipped: no Chrome or Edge found" -ForegroundColor Yellow
} else {
    # the real page with the tests appended, so they see its functions and state
    New-Item -ItemType Directory -Force "$Out\web" | Out-Null
    $pageHtml = [IO.File]::ReadAllText((Join-Path $Root "cast\pc\web\ipad.html"))
    $testJs = [IO.File]::ReadAllText((Join-Path $Tests "web\ipad-tests.js"))
    $i = $pageHtml.LastIndexOf("</body>")
    $html = $pageHtml.Substring(0, $i) + "<script>`n" + $testJs + "`n</script>`n" + $pageHtml.Substring($i)
    $testPage = Join-Path $Out "web\ipad-test.html"
    [IO.File]::WriteAllText($testPage, $html, (New-Object Text.UTF8Encoding($false)))
    $profileDir = Join-Path $Out "web\profile"
    $dom = & $browser --headless=new --disable-gpu --no-first-run --no-default-browser-check "--user-data-dir=$profileDir" `
        --window-size=1080,810 --lang=en-US --virtual-time-budget=3000 --dump-dom ("file:///" + $testPage.Replace('\', '/')) 2>$null | Out-String
    $m = [regex]::Match($dom, '<pre id="test-results">([\s\S]*?)</pre>')
    if (-not $m.Success) {
        Write-Host "  FAIL  no results from the page (a script error before the tests?)"
        $failed += "iPad page"
    } else {
        $res = [System.Net.WebUtility]::HtmlDecode($m.Groups[1].Value).Trim()
        $res -split "`n" | ForEach-Object { Write-Host "  $_" }
        $n = ([regex]::Matches($res, '(?m)^ok ')).Count
        $f = ([regex]::Matches($res, '(?m)^FAIL ')).Count
        Write-Host "iPad page: $n passed, $f failed"
        if ($f -gt 0 -or $res -notmatch 'PASSED') { $failed += "iPad page" }
    }
}

# ---------------------------------------------------------------- PowerShell
Step "PowerShell scripts parse"
$bad = 0
Get-ChildItem $Root -Recurse -Filter *.ps1 | Where-Object { $_.FullName -notmatch '\\(build|out)\\' } | ForEach-Object {
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$null, [ref]$errors)
    $rel = $_.FullName.Substring($Root.Length + 1)
    if ($errors) { $bad++; Write-Host "  FAIL  $rel : $($errors[0].Message) (line $($errors[0].Extent.StartLineNumber))" }
    else { Write-Host "  ok    $rel" }
}
if ($bad -gt 0) { $failed += "PowerShell" }

Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "All tests passed." -ForegroundColor Green
exit 0
