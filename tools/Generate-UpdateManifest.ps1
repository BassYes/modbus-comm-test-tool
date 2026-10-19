<#
.SYNOPSIS
Generate an unsigned update manifest for CommTestTool.
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][long]$Build,
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$DownloadUrl,
    [string]$SigningKeyId = "commtool-update-2026",
    [string]$Notes = "",
    [int]$ExpiresDays = 90
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $PackagePath)) { throw "Package not found: $PackagePath" }

$package = Get-Item $PackagePath
$hash = (Get-FileHash -Path $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant()

$issued = (Get-Date).ToUniversalTime()
$expires = $issued.AddDays($ExpiresDays)

$manifest = [ordered]@{
    schema = 1
    product = "CommTestTool"
    platform = "windows-x64"
    version = $Version
    build = $Build
    issuedAt = $issued.ToString("o")
    expiresAt = $expires.ToString("o")
    fileName = $package.Name
    downloadUrl = $DownloadUrl
    size = $package.Length
    sha256 = $hash
    executableSha256 = $null
    notes = $Notes
    signingKeyId = $SigningKeyId
    signature = $null
}

$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $ManifestPath -Encoding UTF8
Write-Host "Manifest written: $ManifestPath"
Write-Host "Size: $($package.Length), SHA256: $hash"