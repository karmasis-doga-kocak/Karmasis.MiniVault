#Requires -Version 5.1
<#
.SYNOPSIS
    Uninstalls the Karmasis MiniVault Windows service.

.DESCRIPTION
    Stops and deletes the Windows service, then removes the install directory.
    %ProgramData%\MiniVault (which holds the DPAPI-protected master key) is left untouched
    unless -PurgeData is passed. -PurgeData prints a red warning and asks the operator to type PURGE;
    pass -Force to skip that prompt.

    Runs on Windows PowerShell 5.1 and later, from an elevated session.

.EXAMPLE
    .\uninstall.ps1 -ServiceName MiniVaultSmoke -InstallDir C:\Temp\MiniVaultSmoke -PurgeData -Force
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'KarmasisMiniVault',
    [string]$InstallDir = 'C:\Program Files\Karmasis\MiniVault',

    # Also delete %ProgramData%\MiniVault. This destroys the DPAPI master key: without a
    # separately stored recovery key, every secret in the database becomes unrecoverable.
    # Prompts for confirmation unless -Force is also passed.
    [switch]$PurgeData,

    # Skip the interactive PURGE confirmation for -PurgeData (for unattended teardown).
    [switch]$Force
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

# WQL string literals escape a single quote by doubling it. Without this a -ServiceName containing an
# apostrophe produces an invalid query (or, worse, a query with an injected clause).
$serviceNameFilter = "Name='" + ($ServiceName -replace "'", "''") + "'"

Write-Host "Uninstalling MiniVault service '$ServiceName'."

$service = Get-CimInstance -ClassName Win32_Service -Filter $serviceNameFilter -ErrorAction SilentlyContinue
if ($service) {
    Write-Host "Stopping service '$ServiceName'..."
    & sc.exe stop $ServiceName | Out-Null

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $service = Get-CimInstance -ClassName Win32_Service -Filter $serviceNameFilter -ErrorAction SilentlyContinue
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
    Write-Host "DESTRUCTIVE: -PurgeData deletes $programDataDir, including the DPAPI master key." -ForegroundColor Red
    Write-Host "Every secret in the MiniVault database becomes permanently unrecoverable unless the recovery material was saved separately and offline." -ForegroundColor Red
    if (-not $Force) {
        $confirmation = Read-Host "Type PURGE to confirm deleting $programDataDir (anything else aborts)"
        if ($confirmation -ne 'PURGE') {
            Write-Host "Purge not confirmed; keeping $programDataDir."
            Write-Host ''
            Write-Host "MiniVault service '$ServiceName' uninstalled."
            exit 0
        }
    }
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
