<#
.SYNOPSIS
  Publishes the Windows app as single self-contained exes, one per brand,
  into a folder named after the version.

.DESCRIPTION
  Writes dist/<version>/ with, for each brand:
    <Brand>.exe            the app (installs itself when run)
    <Brand>Version.txt     the version, e.g. 1.0.0
    <Brand>Sha.txt         the exe's SHA-256
    <Brand>Signature.txt   the signature over brand, version and SHA-256
  and URLs.txt, the link each file is uploaded to.
  for example WebinCloud.exe, WebinCloudSha.txt, RyzhellCloud.exe, RyzhellCloudSha.txt.
  To publish an update, upload a brand's files as they are to its folder on
  the publisher's server (the links are in Updates/UpdateSource.cs).
  The app installs the exe only when the signature was made with the key from
  new-signing-key.ps1 and the exe it downloaded matches the SHA-256.

.EXAMPLE
  ./scripts/build.bat                          # double-click: asks for the version, builds both brands
  ./scripts/build.ps1                          # same, from PowerShell
  ./scripts/build.ps1 -Version 0.2.0           # both brands as 0.2.0
  ./scripts/build.ps1 -Brand RyzhellCloud      # one brand only
  ./scripts/build.ps1 -SigningKey E:\keys\client-signing-key.blob
#>
param(
    [ValidateSet('All', 'WebinCloud', 'RyzhellCloud')]
    [string]$Brand = 'All',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    # Asked for when not given.
    [string]$Version = '',
    # Asked for when not given. "none" builds without signatures.
    [string]$SigningKey = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\CloudClient\CloudClient.csproj'

$current = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
while ($Version -notmatch '^\d+\.\d+\.\d+$') {
    if ($Version) { Write-Host "  '$Version' is not a version like 1.2.3." -ForegroundColor Yellow }
    $Version = (Read-Host "Version to publish (current $current)").Trim()
}

# The version is also the project's from now on, so debug builds show it too.
if ($Version -ne $current) {
    $text = [System.IO.File]::ReadAllText($project)
    $text = $text -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
    [System.IO.File]::WriteAllText($project, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "CloudClient.csproj: version $current -> $Version"
}

# x64 builds go straight in dist/<version>; other runtimes in a subfolder.
$out = Join-Path $root "dist\$Version"
if ($Runtime -ne 'win-x64') { $out = Join-Path $out $Runtime }
New-Item -ItemType Directory -Force $out | Out-Null

# The public key the apps carry; the signing key must be its pair, or every signature would be refused.
$updateKey = Join-Path $root 'src\CloudClient\Updates\UpdateKey.cs'
$expected = [regex]::Match([System.IO.File]::ReadAllText($updateKey), 'Public = "([^"]+)"').Groups[1].Value
$defaultKey = Join-Path $env:USERPROFILE '.webincloud\client-signing-key.blob'

function Test-SigningKey([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return "no file at $path" }
    try {
        $key = [System.Security.Cryptography.CngKey]::Import([System.IO.File]::ReadAllBytes($path), [System.Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
        $public = [Convert]::ToBase64String($key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob))
        $key.Dispose()
    } catch {
        return "$path is not a signing key"
    }
    if ($public -ne $expected) { return "$path is not the key the apps trust (its public key differs from UpdateKey.cs)" }
    return $null
}

$sign = $true
while ($true) {
    if (-not $SigningKey) {
        $answer = (Read-Host "Signing key path (Enter for $defaultKey, or 'none' to build without signing)").Trim().Trim('"')
        $SigningKey = if ($answer) { $answer } else { $defaultKey }
    }
    if ($SigningKey -eq 'none') { $sign = $false; break }
    $problem = Test-SigningKey $SigningKey
    if (-not $problem) { break }
    Write-Host "  $problem" -ForegroundColor Yellow
    $SigningKey = ''
}
if (-not $sign) { Write-Warning "Building without signing: no Signature.txt files, so apps will not accept these builds as updates." }

$brands = if ($Brand -eq 'All') { @('WebinCloud', 'RyzhellCloud') } else { @($Brand) }
foreach ($b in $brands) {
    $staging = Join-Path $root "artifacts\$b\publish\$Runtime"
    dotnet publish $project -c Release -r $Runtime "-p:Brand=$b" "-p:Version=$Version" `
        --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -o $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $b" }

    $exe = Join-Path $out "$b.exe"
    Copy-Item (Join-Path $staging "$b.exe") $exe -Force
    $sha256 = (Get-FileHash -Algorithm SHA256 $exe).Hash.ToLowerInvariant()
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Join-Path $out "${b}Sha.txt"), "$sha256`n", $utf8)
    [System.IO.File]::WriteAllText((Join-Path $out "${b}Version.txt"), "$Version`n", $utf8)

    if ($sign) {
        # Signed: brand, version and SHA-256, one per line. The app checks this
        # signature against the public key built into it before installing anything.
        $key = [System.Security.Cryptography.CngKey]::Import([System.IO.File]::ReadAllBytes($SigningKey), [System.Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
        $ecdsa = New-Object System.Security.Cryptography.ECDsaCng($key)
        $ecdsa.HashAlgorithm = [System.Security.Cryptography.CngAlgorithm]::Sha256
        $message = [System.Text.Encoding]::UTF8.GetBytes("$b`n$Version`n$sha256")
        $signature = [Convert]::ToBase64String($ecdsa.SignData($message))
        [System.IO.File]::WriteAllText((Join-Path $out "${b}Signature.txt"), "$signature`n", $utf8)
    }
    Write-Host "$b $Version  sha256 $sha256"
}

# Where each file goes, read from the links the apps use (Updates/UpdateSource.cs).
$source = [System.IO.File]::ReadAllText((Join-Path $root 'src\CloudClient\Updates\UpdateSource.cs'))
$folders = @{
    RyzhellCloud = [regex]::Match($source, '#if BRAND_RYZHELLCLOUD\s+private const string Folder = "([^"]+)"').Groups[1].Value
    WebinCloud   = [regex]::Match($source, '#else\s+private const string Folder = "([^"]+)"').Groups[1].Value
}
$urls = @("Version $Version - upload each file to its link.", "")
foreach ($b in $brands) {
    $urls += "$b"
    foreach ($file in @("$b.exe", "${b}Version.txt", "${b}Sha.txt", "${b}Signature.txt")) {
        if ($file -like '*Signature.txt' -and -not $sign) { continue }
        $urls += "  $file"
        $urls += "    $($folders[$b])$file/download?inline=1&path="
    }
    $urls += ""
}
[System.IO.File]::WriteAllLines((Join-Path $out 'URLs.txt'), [string[]]$urls, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Done: $out"
Write-Host "To publish it, upload each brand's files to its folder: Apps/Dist/WebinCloudClient and Apps/Dist/RyzhellCloudClient."
