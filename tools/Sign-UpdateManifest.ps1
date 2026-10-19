<#
.SYNOPSIS
Sign an update manifest using Comm.UpdateSigner (Ed25519).
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Manifest,
    [Parameter(Mandatory = $true)]
    [string]$Key,
    [string]$Out = ""
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = 'C:/Users/Bass/.dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$args = @('run', '--project', (Join-Path $repoRoot 'tools\Comm.UpdateSigner\Comm.UpdateSigner.csproj'), '--', 'sign', '--manifest', $Manifest, '--key', $Key)
if ($Out) { $args += @('--out', $Out) }

& $dotnet @args
if ($LASTEXITCODE -ne 0) { throw "Sign-UpdateManifest failed with exit code $LASTEXITCODE" }
