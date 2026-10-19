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
    [int]$ExpiresDays = 90,
    [string]$ExecutableName = "CommTestTool.exe"
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $PackagePath)) { throw "Package not found: $PackagePath" }

$package = Get-Item $PackagePath
$hash = (Get-FileHash -Path $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant()

# Compute executable hash from zip if possible.
$exeHash = $null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    $entry = $zip.Entries | Where-Object { $_.FullName -eq $ExecutableName -or $_.FullName -like "*/$ExecutableName" } | Select-Object -First 1
    if ($entry) {
        $stream = $entry.Open()
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $bytes = New-Object byte[] 8192
        $ms = New-Object System.IO.MemoryStream
        while (($read = $stream.Read($bytes, 0, $bytes.Length)) -gt 0) { $ms.Write($bytes, 0, $read) }
        $ms.Position = 0
        $exeHash = ([System.BitConverter]::ToString($sha.ComputeHash($ms))).Replace("-", "")).ToLowerInvariant()
        $stream.Dispose()
        $ms.Dispose()
    }
    $zip.Dispose()
} catch {
    # Optional field.
}

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
    executableSha256 = $exeHash
    notes = $Notes
    signingKeyId = $SigningKeyId
    signature = $null
}

$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $ManifestPath -Encoding UTF8
Write-Host "Manifest written: $ManifestPath"
Write-Host "Size: $($package.Length), SHA256: $hash"
