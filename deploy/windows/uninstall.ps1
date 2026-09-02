<#
.SYNOPSIS
    Uninstalls the Karmasis MiniVault Windows service.

.DESCRIPTION
    Stops and deletes the Windows service, then removes the install directory.
    %ProgramData%\MiniVault (which holds the DPAPI-protected master key) is left untouched
    unless -PurgeData is passed.

.EXAMPLE
    .\uninstall.ps1 -ServiceName MiniVaultSmoke -InstallDir C:\Temp\MiniVaultSmoke -PurgeData
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'KarmasisMiniVault',
    [string]$InstallDir = 'C:\Program Files\Karmasis\MiniVault',

    # Also delete %ProgramData%\MiniVault. This destroys the DPAPI master key: without a
    # separately stored recovery key, every secret in the database becomes unrecoverable.
    [switch]$PurgeData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    Write-Error 'uninstall.ps1 must be run from an elevated (Administrator) PowerShell session.'
    exit 1
}

$programDataDir = Join-Path $env:ProgramData 'MiniVault'

Write-Host "Uninstalling MiniVault service '$ServiceName'."

$service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
if ($service) {
    Write-Host "Stopping service '$ServiceName'..."
    & sc.exe stop $ServiceName | Out-Null

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
        if (-not $service -or $service.State -eq 'Stopped') { break }
        Start-Sleep -Seconds 1
    }

    Write-Host "Deleting service '$ServiceName'..."
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "sc.exe delete returned exit code $LASTEXITCODE for service '$ServiceName'."
    }
} else {
    Write-Warning "Service '$ServiceName' was not found; skipping stop/delete."
}

if (Test-Path $InstallDir) {
    Write-Host "Removing install directory '$InstallDir'..."
    Remove-Item -Path $InstallDir -Recurse -Force
} else {
    Write-Warning "Install directory '$InstallDir' was not found; nothing to remove."
}

if ($PurgeData) {
    Write-Host ''
    Write-Warning "PURGING $programDataDir. This deletes the DPAPI master key. Every secret in the MiniVault database becomes permanently unrecoverable unless the recovery material was saved separately and offline."
    if (Test-Path $programDataDir) {
        Remove-Item -Path $programDataDir -Recurse -Force
        Write-Host "Removed $programDataDir."
    } else {
        Write-Warning "$programDataDir was not found; nothing to purge."
    }
} else {
    Write-Host ''
    Write-Host "Keeping $programDataDir (master key and machine configuration). Pass -PurgeData to remove it."
}

Write-Host ''
Write-Host "MiniVault service '$ServiceName' uninstalled."
exit 0
