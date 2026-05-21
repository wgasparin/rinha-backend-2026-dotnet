#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Roda o loader (gera data/references_v1.bin a partir de resources/references.json.gz)
    e em seguida o benchmark da busca IVF.

.PARAMETER Force
    Regenera o .bin mesmo que ja exista.

.PARAMETER InputPath
    Caminho do dataset gzipado. Default: resources/references.json.gz

.PARAMETER OutputPath
    Caminho do .bin gerado. Default: data/references_v1.bin
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [string]$InputPath  = (Join-Path $PSScriptRoot 'resources\references.json.gz'),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'data\references_v1.bin')
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$loaderProj = Join-Path $PSScriptRoot 'loader\loader.csproj'
$benchProj  = Join-Path $PSScriptRoot 'bench\bench.csproj'

if (-not (Test-Path $InputPath)) {
    Write-Error "dataset nao encontrado: $InputPath"
    exit 1
}

$needsLoader = $Force -or -not (Test-Path $OutputPath)

if ($needsLoader) {
    Write-Host "==> loader: $InputPath -> $OutputPath" -ForegroundColor Cyan
    $outDir = Split-Path -Parent $OutputPath
    if (-not (Test-Path $outDir)) {
        New-Item -ItemType Directory -Path $outDir | Out-Null
    }

    dotnet run --project $loaderProj -c Release -- --input $InputPath --output $OutputPath
    if ($LASTEXITCODE -ne 0) {
        Write-Error "loader falhou (exit $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
} else {
    Write-Host "==> .bin ja existe em $OutputPath (use -Force para regenerar)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "==> bench: $OutputPath" -ForegroundColor Cyan
dotnet run --project $benchProj -c Release -- $OutputPath
if ($LASTEXITCODE -ne 0) {
    Write-Error "bench falhou (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}
