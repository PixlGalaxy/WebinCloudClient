<#
.SYNOPSIS
  Publishes the Windows app as a single self-contained exe for one brand.

.EXAMPLE
  ./scripts/build.ps1                      # Webin Cloud
  ./scripts/build.ps1 -Brand RyzhellCloud  # Ryzhell Cloud
#>
param(
    [ValidateSet('WebinCloud', 'RyzhellCloud')]
    [string]$Brand = 'WebinCloud',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist\$Brand\$Runtime"

dotnet publish (Join-Path $root 'src\CloudClient\CloudClient.csproj') `
    -c Release `
    -r $Runtime `
    -p:Brand=$Brand `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "Built $(Join-Path $out "$Brand.exe")"
