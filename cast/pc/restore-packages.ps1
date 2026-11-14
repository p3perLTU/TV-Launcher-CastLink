# Downloads the libraries the PC sender's WebRTC (iPad over UDP) needs, from nuget.org only,
# at exactly these versions, and checks each package's SHA-512 against the NuGet catalog before
# using it. The DLLs for .NET Framework end up in cast\pc\lib (git-ignored, like packages\).
#   powershell -ExecutionPolicy Bypass -File .\restore-packages.ps1
# Main library: SIPSorcery (BSD-3-Clause with an additional geographic use restriction, see
# LICENSE.md in its package); the rest are its dependencies.
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$Root = Split-Path (Split-Path $PSScriptRoot)
$Pkgs = Join-Path $Root "packages"
$Lib = Join-Path $PSScriptRoot "lib"

# id, version, SHA-512 of the .nupkg (base64): SIPSorcery 10.0.17 and the lowest versions its
# .NET Framework 4.6.2 build asks for. The hashes are pinned here as well, so a package that
# changed on nuget.org later (or a catalog that lies) is refused: both must agree.
$Packages = @(
    @("SIPSorcery", "10.0.17", "DxbnP0bx2WfjtuvxspRIPKMd9hmVxKilQme53kqP7abBXFPNlcWlbsdtVxA6py+PzxwdhSNoP4RETsGHCJZ9WQ=="),
    @("SIPSorceryMedia.Abstractions", "10.0.17", "YiFSiOvAgbzZCLI1AM0Lu+zF8TZcz1VZRF/FVZVUxcEf/nbdBPklxoOchYIG4Je4qU+2pwn+DwoWWZ7HTx448A=="),
    @("BouncyCastle.Cryptography", "2.7.0", "Lys+dJthpD8quONaAkRV9ke8DgET0CwBW91NeJjMVHZGvRRNO8DkdReYFfCBOCcvMpzJeC3DuLrpRuntYnq9Iw=="),
    @("Concentus", "2.2.2", "j0S91LceGglxRrfdXKOiz2Qhk3Rq83dZnw9vTgE155QUJWw8Nw381vTng0YgbSsrVAYDzY1JENsFR8nwqdTb/g=="),
    @("DnsClient", "1.8.0", "Rfd5PQ6HZrp5CcDfFE//BDryq4uWHdoe0zeHMovoaaLHYJJ+IoArwp4MndSl2jWQBoVDrZXu+X6QSnh72lKYqg=="),
    @("Makaretu.Dns.Multicast", "0.27.0", "kLvp+ujJABP34QcRcjyH0ltApkJ5rru5dVSR7io/RZmWBQ1Fb1HG5yjJeHN0A9NqIMEEtumJaFdJjGaP9rSfWg=="),
    @("Makaretu.Dns", "2.0.1", "ammpQdO1MPZk+PDG8UlfWgMpSVof7CIL65xQKOyGd/d9NbKQXrq9Yz/4n3warlqnte1QjfURMrKKysC4WskA8Q=="),
    @("SimpleBase", "1.3.1", "herNBiKvpGYnN3TVGQzDvSLSRQKRi0xhfhblw7V/zSMAwhz0OsWkdbgaKwwOGVVYsh4R/4GBOvA0TcPNg84QuA=="),
    @("IPNetwork2", "2.1.2", "2RvY7XFQ/BoVOZAgbnrdP9QtPj8S3f2xFS884o0OjvvEX6dmdVQyEilc0UDOY7wLFIGhYNnRNPUEE96kd+jOLg=="),
    @("Common.Logging", "3.4.1", "B7mOrLAiALcadDoMzuKyMEYbdfLUUixeZVbBQqn1t0Oyolz7LaBC6OyVOC094S8XfOxx+RImYf7r9O5NcLhlug=="),
    @("Common.Logging.Core", "3.4.1", "6F/bYCvIZePJf7WeCqgMVCY9mvuM4VlKY+yZRqalsLddMQSqGp2lgs0yowObmNUlOsK7SxrX/cZrSLKSzwTEQw=="),
    @("Microsoft.Bcl.HashCode", "6.0.0", "k0mXL9QMC7IN4nRuAHuLWykH8H/ng04IWmGSj72aqlaHztvyA6NNc90Nipuavy1iqByVwiJVoIDkNH/IlcnfLQ=="),
    @("SIPSorcery.WebSocketSharp", "0.0.1", "0Y6ajEBaABPzDyRHNx35a44wGJX7wY11FyobjVNhmsmhK5Fk73IqMU39EFSsHyNSccVD2B2IBRqQBQpnDOPu/A=="),
    @("System.Memory", "4.6.3", "NXcNYlWoXe5cz9sb8Huo6x2dCZVYkhwKtgE00n/MoI8V4ZI/7/t+EI5bOhQFlZfFjjqM8+U6prjU/aARt7H/tA=="),
    @("System.Buffers", "4.5.1", "gNphWOVbm89+C15jebnPRaYykU8De1PFv1YJV24814IfeGGVa3PXRHDS0MLlbdI1pe9Mpv/n4ZK4INwtAjqv8g=="),
    @("System.Numerics.Vectors", "4.5.0", "nATsBTD2CKr4AYN6eRszhX4sptImWmBJwB/U6XKCWWfnCcrTBw8XSCm3QA9gjppkHTr8OkXUY21MR91D3QZXsw=="),
    @("System.Runtime.CompilerServices.Unsafe", "6.1.2", "t2aXWJZBkAkRrTOnw31OBELKEVSDD5YvC3O5dXaHFsR66/nRTKm1y3Iq6NwFI5u5IlKrWYfdan66V+GKKkY8hQ=="),
    @("System.ValueTuple", "4.6.2", "sMfnKIAepEOav44iCUQAECVXLA1/TVNC37g+mxo690/BFiHzbuHPFvdyJ6MdED4CL7iq6N7kllbMUVi5/nWJiA=="),
    @("System.Threading.Tasks.Extensions", "4.6.3", "zWRHXIBnbfzQE1SamNoW9X5NjEcW/JNAtvVxGKd3bcg71wQVmoI3pDq+WUa2A+temXSNCm7707hmAFwwcYlK0A=="),
    @("Microsoft.Bcl.AsyncInterfaces", "10.0.11", "J+qSwHtmg8G3mDZb1vDQy3rnRAqwuSJNGn9UPCwHgwXCr5MSNwy7MRv8u7f0T8m84woNa7mhQBCz7fU74eoinQ=="),
    @("Microsoft.Extensions.Logging.Abstractions", "10.0.11", "WoC6bSmOB6O55mX3pTQ1bXbcw/eoW8MKs05kAyLMwVPtOgP4B/fOqGXCHO/fvezfu7YfOXEo6midr89oFLtitw=="),
    @("Microsoft.Extensions.DependencyInjection.Abstractions", "10.0.11", "6DaP+fqx3GBGLGXCB8jfvbQ2kTKZjQxxwZpT8SroC59FEO0FviggiIOpvLbLXd9nnFnwKtENi20lE8qRAt+9XA=="),
    @("System.Diagnostics.DiagnosticSource", "10.0.11", "8vVltm6+m+s4jdtOuOxJ7ewGnQJVrJUOhYM93gp3sBg5k5a6nVMsI+dPlb3znHxeQp+UQr9snkJYi1dw5FrC2g=="),
    @("Microsoft.Win32.Registry", "5.0.0", "Rx5mVnzlnMhkda7OeBXQUmEmTOEU4MFoi6JVHdUUlJAfpy3XqPdPjo8PPbp0r4WV8XdVLzwGq7S/znZpIZcHbg=="),
    @("System.Security.AccessControl", "5.0.0", "rmsDrQKdPraBimyLtWz0kEAT+lNaZ7jmIbeDoCndiKouRx4ALLx9cgOBrYvIxrkxEaCPbOLSca9tl0v00CtsgQ=="),
    @("System.Security.Principal.Windows", "5.0.0", "RKkgqq8ishctQTGbtXqyuOGkUx1fAhkqb1OoHYdRJRlbYLoLWkSkWYHRN/17DzplsSlZtf2Xr8BXjNhO8nRnzQ==")
)
# (System.Net.Security, System.Net.WebSockets.Client and the other System.* 4.x packages in
#  SIPSorcery's list are part of .NET Framework 4.7.2+ itself and are not copied.)

$Frameworks = "net462", "net461", "net46", "net452", "net45", "netstandard2.0", "netstandard1.3", "netstandard1.0"

New-Item -ItemType Directory -Force $Pkgs, $Lib | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($p in $Packages) {
    $id = $p[0]; $ver = $p[1]; $lid = $id.ToLower()
    $file = Join-Path $Pkgs "$lid.$ver.nupkg"
    $leaf = Invoke-RestMethod "https://api.nuget.org/v3/registration5-gz-semver2/$lid/$ver.json"
    $cat = Invoke-RestMethod $leaf.catalogEntry
    if (-not (Test-Path $file)) {
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$lid/$ver/$lid.$ver.nupkg" -OutFile $file -UseBasicParsing
    }
    $sha = [Convert]::ToBase64String([Security.Cryptography.SHA512]::Create().ComputeHash([IO.File]::ReadAllBytes($file)))
    if ($cat.packageHashAlgorithm -ne "SHA512" -or $sha -ne $cat.packageHash -or $sha -ne $p[2]) {
        Remove-Item $file
        throw "$id $ver : SHA-512 does not match the NuGet catalog and the hash pinned here - deleted, nothing used"
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($file)
    try {
        $picked = $null
        foreach ($fw in $Frameworks) {
            $dlls = @($zip.Entries | Where-Object { $_.FullName -like "lib/$fw/*.dll" })
            if ($dlls.Count -gt 0) { $picked = $fw; break }
        }
        if (-not $picked) { "{0,-52} {1,-9} verified, no .NET Framework DLL (not needed)" -f $id, $ver; continue }
        foreach ($e in $dlls) { [IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $Lib $e.Name), $true) }
        "{0,-52} {1,-9} verified, {2}: {3}" -f $id, $ver, $picked, (($dlls | ForEach-Object { $_.Name }) -join ", ")
    } finally { $zip.Dispose() }
}
# Licences go next to the DLLs (they are shipped together): every licence file a package carries
# goes to lib\licenses\, and lib\THIRD-PARTY-NOTICES.md lists each package, its licence and
# copyright. Packages whose nuspec only has a licence URL (or nothing) are listed by hand here.
$KnownLicence = @{
    "Common.Logging" = "Apache-2.0"; "Common.Logging.Core" = "Apache-2.0"; "SimpleBase" = "Apache-2.0"
    "Makaretu.Dns" = "MIT"; "Makaretu.Dns.Multicast" = "MIT"; "IPNetwork2" = "BSD-2-Clause"
    "System.Buffers" = "MIT"; "System.Numerics.Vectors" = "MIT"; "Concentus" = "BSD-3-Clause"
    "SIPSorcery" = "BSD-3-Clause + use restriction"; "SIPSorceryMedia.Abstractions" = "BSD-3-Clause + use restriction"
}
$LicDir = Join-Path $Lib "licenses"
New-Item -ItemType Directory -Force $LicDir | Out-Null
$rows = @()
foreach ($p in $Packages) {
    $id = $p[0]; $ver = $p[1]
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $Pkgs "$($id.ToLower()).$ver.nupkg"))
    try {
        $ns = $zip.Entries | Where-Object { $_.FullName -like "*.nuspec" } | Select-Object -First 1
        $r = New-Object IO.StreamReader($ns.Open()); $m = ([xml]$r.ReadToEnd()).package.metadata; $r.Close()
        $lic = if ($m.license -and $m.license.type -eq "expression") { $m.license.'#text' } elseif ($KnownLicence.ContainsKey($id)) { $KnownLicence[$id] } else { "see file" }
        $files = @()
        foreach ($e in $zip.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.Name -match '^(LICEN[SC]E|THIRD-PARTY-NOTICES)' }) {
            $name = "$id-$($e.Name)"
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $LicDir $name), $true)
            $files += "[$name](licenses/$name)"
        }
        if ($lic -eq "Apache-2.0") { $files += "[Apache-2.0.txt](licenses/Apache-2.0.txt)" }
        if ($lic -eq "MIT" -and $files.Count -eq 0) { $files += "[MIT.txt](licenses/MIT.txt) with the copyright line" }
        if ($lic -eq "BSD-2-Clause" -and $files.Count -eq 0) { $files += "[BSD-2-Clause.txt](licenses/BSD-2-Clause.txt) with the copyright line" }
        $url = if ($m.projectUrl) { $m.projectUrl } else { "https://www.nuget.org/packages/$id" }
        $rows += "| [$id]($url) | $ver | $lic | $(($m.copyright -replace '\|', '/' -replace '\s+', ' ').Trim()) | $($files -join ', ') |"
    } finally { $zip.Dispose() }
}
Copy-Item (Join-Path $Root "licenses\Material-Symbols-Apache-2.0.txt") (Join-Path $LicDir "Apache-2.0.txt") -Force
Copy-Item (Join-Path $Root "licenses\MIT.txt") (Join-Path $LicDir "MIT.txt") -Force
Copy-Item (Join-Path $Root "licenses\BSD-2-Clause.txt") (Join-Path $LicDir "BSD-2-Clause.txt") -Force
$notes = @"
# Third-party notices

``CastLink.exe`` (MIT, see the project's LICENSE) loads these libraries from this ``lib``
folder for WebRTC (picture and sound to the iPad over UDP). Each keeps its own licence.

> [!IMPORTANT]
> **SIPSorcery** is BSD-3-Clause **with an additional use restriction**: it must not be used,
> modified or distributed inside Israel and the Occupied Territories. Read
> [licenses/SIPSorcery-LICENSE.md](licenses/SIPSorcery-LICENSE.md) before sharing the app.

| Package | Version | Licence | Copyright | Licence text |
|---|---|---|---|---|
$($rows -join "`n")
"@
Set-Content (Join-Path $Lib "THIRD-PARTY-NOTICES.md") $notes -Encoding UTF8
"Done: $((Get-ChildItem $Lib -Filter *.dll).Count) DLLs in $Lib, licences in $LicDir"
