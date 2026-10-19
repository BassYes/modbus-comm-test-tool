<#
.SYNOPSIS
Verify an update manifest using Comm.UpdateSigner (Ed25519).
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Manifest,
    [string]$PublicKeyFile = "",
    [string]$PublicKeyBase64 = ""
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$args = @('run', '--project', (Join-Path $repoRoot 'tools\Comm.UpdateSigner\Comm.UpdateSigner.csproj'), '--', 'verify', '--manifest', $Manifest)
if ($PublicKeyFile) { $args += @('--pubkey', $PublicKeyFile) }
if ($PublicKeyBase64) { $args += @('--pubkey-base64', $PublicKeyBase64) }

& $dotnet @args
if ($LASTEXITCODE -ne 0) { throw "Verify-UpdateManifest failed with exit code $LASTEXITCODE" }
