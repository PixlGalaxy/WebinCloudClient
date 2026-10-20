<#
.SYNOPSIS
  Creates the key that signs app updates, once per publisher.

.DESCRIPTION
  Writes the private key to -Path (outside the repo; back it up, it cannot be
  recovered) and prints the public key to paste into
  src/CloudClient/Updates/UpdateKey.cs. The app installs an update only when
  its manifest carries a signature this key made, so a compromised server or
  network cannot push a different exe.

.EXAMPLE
  ./scripts/new-signing-key.ps1
#>
param(
    [string]$Path = (Join-Path $env:USERPROFILE '.webincloud\client-signing-key.blob')
)

$ErrorActionPreference = 'Stop'
if (Test-Path $Path) { throw "A key already exists at $Path. Delete it first if you really want a new one: apps already installed would stop accepting updates." }

$parameters = New-Object System.Security.Cryptography.CngKeyCreationParameters
$parameters.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
$key = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, $null, $parameters)

New-Item -ItemType Directory -Force (Split-Path -Parent $Path) | Out-Null
[System.IO.File]::WriteAllBytes($Path, $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob))
$public = [Convert]::ToBase64String($key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob))

Write-Host "Private key written to $Path - keep a backup somewhere safe."
Write-Host "Public key (paste into src/CloudClient/Updates/UpdateKey.cs):"
Write-Host $public
