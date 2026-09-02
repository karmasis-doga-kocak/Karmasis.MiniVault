#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the karmasis/minivault:dev image using nupkgs from the local-nuget folder feed.

.DESCRIPTION
    The repo restores Karmasis.Cryptography only from ..\..\local-nuget (see nuget.config),
    a path that does not exist inside a Docker build context. This script bridges that gap
    for local development: it copies the *.nupkg files from -LocalFeed into <repo>/packages
    (git-ignored, consumed by docker/Dockerfile via docker/nuget.docker.config), runs the
    Docker build from the repo root, and always removes the staged packages afterwards -
    even if the build fails.

.PARAMETER LocalFeed
    Folder containing the *.nupkg files to stage. Defaults to ..\..\local-nuget relative to
    the repository root (i.e. D:\Karmasis\local-nuget for a checkout at
    D:\Karmasis\repos\Karmasis.MiniVault).

.PARAMETER ImageTag
    Tag applied to the built image. Defaults to karmasis/minivault:dev.

.EXAMPLE
    .\docker\build-local.ps1

.EXAMPLE
    .\docker\build-local.ps1 -LocalFeed D:\Karmasis\local-nuget -ImageTag karmasis/minivault:dev
#>
[CmdletBinding()]
param(
    # Resolved below: $PSScriptRoot is empty inside the param block when the script is started with
    # `powershell -File <relative path>` on Windows PowerShell 5.1.
    [string]$LocalFeed = "",
    [string]$ImageTag = "karmasis/minivault:dev"
)

$ErrorActionPreference = "Stop"

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $LocalFeed) { $LocalFeed = Join-Path $scriptDir "..\..\..\local-nuget" }

$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
$packagesDir = Join-Path $repoRoot "packages"

$resolvedFeed = Resolve-Path -LiteralPath $LocalFeed -ErrorAction Stop
Write-Host "Local NuGet feed: $resolvedFeed"
Write-Host "Repo root:        $repoRoot"
Write-Host "Image tag:        $ImageTag"

$nupkgs = Get-ChildItem -LiteralPath $resolvedFeed -Filter "*.nupkg" -File -ErrorAction Stop
if (-not $nupkgs) {
    throw "No .nupkg files found in '$resolvedFeed'."
}

New-Item -ItemType Directory -Force -Path $packagesDir | Out-Null

try {
    foreach ($pkg in $nupkgs) {
        Copy-Item -LiteralPath $pkg.FullName -Destination $packagesDir -Force
        Write-Host "Staged $($pkg.Name)"
    }

    Push-Location $repoRoot
    try {
        docker build -f docker/Dockerfile -t $ImageTag .
        if ($LASTEXITCODE -ne 0) {
            throw "docker build failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    # Clean the staged nupkgs back out; keep the .gitkeep placeholder so the folder
    # (and hence the Docker build context) still exists for the next build.
    Get-ChildItem -LiteralPath $packagesDir -File |
        Where-Object { $_.Name -ne ".gitkeep" } |
        Remove-Item -Force
    Write-Host "Cleaned up $packagesDir"
}

Write-Host "Built $ImageTag"
