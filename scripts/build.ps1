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
  for example WebinCloud.exe, WebinCloudSha.txt, RyzhellCloud.exe, RyzhellCloudSha.txt.
  To publish an update, upload these files as they are to
  https://webincloud.itsgalaxy.com/updates/ (see Updates/UpdateSource.cs).
  The app installs the exe only when the signature was made with the key from
  new-signing-key.ps1 and the exe it downloaded matches the SHA-256.

.EXAMPLE
  ./scripts/build.bat                          # double-click: asks for the version, builds both brands
  ./scripts/build.ps1                          # same, from PowerShell
  ./scripts/build.ps1 -Version 0.2.0           # both brands as 0.2.0
  ./scripts/build.ps1 -Brand RyzhellCloud      # one brand only
#>
param(
    [ValidateSet('All', 'WebinCloud', 'RyzhellCloud')]
    [string]$Brand = 'All',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    # Asked for when not given.
    [string]$Version = '',
    [string]$SigningKey = (Join-Path $env:USERPROFILE '.webincloud\client-signing-key.blob')
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

$sign = Test-Path $SigningKey
if (-not $sign) { Write-Warning "No signing key at ${SigningKey}: no Signature.txt files written, so apps will not accept these builds as updates. Run ./scripts/new-signing-key.ps1 once." }

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

Write-Host "Done: $out"
Write-Host "To publish it, upload these files to https://webincloud.itsgalaxy.com/updates/"
