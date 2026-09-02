<#
.SYNOPSIS
    Installs the Karmasis MiniVault server as a Windows service.

.DESCRIPTION
    1. Copies the publish output into the install directory and creates the ProgramData folder.
    2. Writes the machine-wide configuration file (connection string, master key provider, TLS).
    3. Locks down the ProgramData folder with a protected ACL.
    4. Runs 'minivault.exe init' and requires the operator to confirm the recovery material is saved.
    5. Registers and starts the Windows service.
    6. Waits for the health endpoint to respond.

    Must be run from an elevated (Administrator) PowerShell session, except with -WhatIfMode, which
    prints the plan and exits without making any changes (and does not require elevation).

.EXAMPLE
    .\install.ps1 -SourceDir C:\publish\minivault -ConnectionString "Server=sql01;Database=MiniVault;Integrated Security=true" -CertificatePath C:\certs\minivault.pfx -CertificatePassword (Read-Host -AsSecureString | ConvertFrom-SecureString)

.EXAMPLE
    .\install.ps1 -WhatIfMode -SourceDir C:\publish\minivault -ConnectionString "Server=sql01;Database=MiniVault;Integrated Security=true" -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\Program Files\Karmasis\MiniVault',

    # Folder produced by `dotnet publish src/MiniVault.Server -p:PublishProfile=win-x64`.
    [string]$SourceDir,

    # Connection string for the MiniVault database. Required.
    [string]$ConnectionString,

    # Account the Windows service runs as. LocalSystem, NetworkService and LocalService need no password.
    [string]$ServiceAccount = 'LocalSystem',
    [string]$ServiceAccountPassword,

    [ValidateSet('single', 'shamir')]
    [string]$Recovery = 'single',
    [int]$Shares,
    [int]$Threshold,

    # Optional: derive the master key from a password instead of generating a random one.
    [string]$MasterKeyPassword,

    # Certificate: exactly one of -CertificatePath (+ -CertificatePassword) or -CertificateThumbprint.
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$CertificateThumbprint,

    [string]$Url = 'https://0.0.0.0:8200',
    [string]$ServiceName = 'KarmasisMiniVault',

    # Skip the "type SAVED to continue" prompt after 'minivault.exe init' (prints a warning instead).
    [switch]$NonInteractive,

    # Print the install plan and exit 0 without making any changes. Does not require elevation.
    [switch]$WhatIfMode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Parameter validation (runs before the elevation check so -WhatIfMode and CI
# smoke tests get a clean non-zero exit + message without needing to elevate).
# ---------------------------------------------------------------------------
$validationErrors = [System.Collections.Generic.List[string]]::new()

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    $validationErrors.Add('-ConnectionString is required.')
}
if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $validationErrors.Add('-SourceDir is required (the folder produced by dotnet publish).')
}

$hasCertPath = -not [string]::IsNullOrWhiteSpace($CertificatePath)
$hasCertThumbprint = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
if ($hasCertPath -and $hasCertThumbprint) {
    $validationErrors.Add('Specify only one of -CertificatePath or -CertificateThumbprint, not both.')
} elseif (-not $hasCertPath -and -not $hasCertThumbprint) {
    $validationErrors.Add('Specify exactly one of -CertificatePath (with -CertificatePassword) or -CertificateThumbprint.')
}

if ($Recovery -eq 'shamir') {
    if ($Shares -lt 2 -or $Threshold -lt 2 -or $Threshold -gt $Shares -or $Shares -gt 255) {
        $validationErrors.Add('-Recovery shamir requires -Shares and -Threshold (both >= 2, Threshold <= Shares <= 255).')
    }
}

$parsedUrl = $null
try {
    $parsedUrl = [Uri]$Url
    if ($parsedUrl.Scheme -ne 'https') { throw "scheme '$($parsedUrl.Scheme)' is not https" }
} catch {
    $validationErrors.Add("-Url '$Url' must be an absolute https:// URL, e.g. https://0.0.0.0:8200.")
}

if ($validationErrors.Count -gt 0) {
    foreach ($message in $validationErrors) { Write-Error $message }
    exit 1
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

# #Requires -RunAsAdministrator cannot be made conditional, and -WhatIfMode must work unelevated
# (it is also how InstallScriptTests exercises this script in CI). Enforce elevation manually instead.
if (-not $WhatIfMode -and -not (Test-IsAdministrator)) {
    Write-Error 'install.ps1 must be run from an elevated (Administrator) PowerShell session. Re-run as Administrator, or pass -WhatIfMode to preview the plan without elevation.'
    exit 1
}

$port = $parsedUrl.Port
$programDataDir = Join-Path $env:ProgramData 'MiniVault'
$machineConfigPath = Join-Path $programDataDir 'appsettings.json'
$wellKnownServiceAccounts = @('LocalSystem', 'NetworkService', 'LocalService', 'NT AUTHORITY\LocalService', 'NT AUTHORITY\NetworkService')
$serviceAccountNeedsPassword = -not ($wellKnownServiceAccounts -contains $ServiceAccount)

Write-Host "MiniVault install plan"
Write-Host "  Service name  : $ServiceName"
Write-Host "  Install dir   : $InstallDir"
Write-Host "  Source dir    : $SourceDir"
Write-Host "  ProgramData   : $programDataDir"
Write-Host "  Service acct  : $ServiceAccount"
Write-Host "  URL           : $Url"
Write-Host ''

# ---------------------------------------------------------------------------
# Step 1: copy the publish output, create the ProgramData folder.
# ---------------------------------------------------------------------------
Write-Host "Step 1: Copy '$SourceDir' to '$InstallDir' (robocopy /MIR) and create '$programDataDir'."
if (-not $WhatIfMode) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    New-Item -ItemType Directory -Path $programDataDir -Force | Out-Null
    & robocopy.exe $SourceDir $InstallDir /MIR /NFL /NDL /NJH /NJS /NC /NS | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed copying '$SourceDir' to '$InstallDir' (exit code $LASTEXITCODE)."
    }
}

# ---------------------------------------------------------------------------
# Step 2: write the machine-wide appsettings.json.
# ---------------------------------------------------------------------------
Write-Host "Step 2: Write $machineConfigPath (ConnectionStrings:MiniVault, MasterKey:Provider=Dpapi, Tls)."
if (-not $WhatIfMode) {
    if ($ConnectionString -match 'Password\s*=') {
        Write-Warning "The connection string contains a Password= value; it will be stored in plain text in $machineConfigPath. That file is ACL-protected in Step 3, but Windows/Integrated authentication avoids storing a secret at all."
    }

    $certificateSection = if ($hasCertThumbprint) {
        [ordered]@{
            Path       = $null
            Password   = $null
            Thumbprint = $CertificateThumbprint
            StoreName  = 'My'
            StoreLocation = 'LocalMachine'
        }
    } else {
        [ordered]@{
            Path       = $CertificatePath
            Password   = $CertificatePassword
            Thumbprint = $null
            StoreName  = 'My'
            StoreLocation = 'LocalMachine'
        }
    }

    $config = [ordered]@{
        ConnectionStrings = [ordered]@{ MiniVault = $ConnectionString }
        MasterKey         = [ordered]@{ Provider = 'Dpapi' }
        Tls               = [ordered]@{
            Url         = $Url
            Certificate = $certificateSection
        }
    }

    ($config | ConvertTo-Json -Depth 6) | Set-Content -Path $machineConfigPath -Encoding utf8
}

# ---------------------------------------------------------------------------
# Step 3: lock down %ProgramData%\MiniVault.
# ---------------------------------------------------------------------------
$aclDescription = 'SYSTEM, Administrators'
if ($serviceAccountNeedsPassword) { $aclDescription += ", and $ServiceAccount" }
Write-Host "Step 3: Apply a protected ACL to $programDataDir ($aclDescription)."
if (-not $WhatIfMode) {
    $grants = @('SYSTEM:(OI)(CI)F', 'Administrators:(OI)(CI)F')
    if ($serviceAccountNeedsPassword) { $grants += "$($ServiceAccount):(OI)(CI)F" }

    & icacls.exe $programDataDir /inheritance:r /grant:r @grants | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "icacls failed to protect '$programDataDir' (exit code $LASTEXITCODE)."
    }
}

# ---------------------------------------------------------------------------
# Step 4: initialize the vault.
# ---------------------------------------------------------------------------
Write-Host "Step 4: Run 'minivault.exe init --recovery $Recovery' and confirm the recovery material has been saved."
if (-not $WhatIfMode) {
    $exePath = Join-Path $InstallDir 'minivault.exe'
    $timestamp = Get-Date -Format 'yyyyMMddHHmmss'
    $outFile = Join-Path $programDataDir "recovery-$timestamp.txt"

    $initArgs = @('init', '--recovery', $Recovery, '--out', $outFile)
    if ($Recovery -eq 'shamir') { $initArgs += @('--shares', "$Shares", '--threshold', "$Threshold") }
    if (-not [string]::IsNullOrWhiteSpace($MasterKeyPassword)) { $initArgs += @('--master-key', $MasterKeyPassword) }

    $initOutput = & $exePath @initArgs 2>&1
    $initExitCode = $LASTEXITCODE
    $initOutput | ForEach-Object { Write-Host $_ }
    if ($initExitCode -ne 0) {
        throw "minivault.exe init failed with exit code $initExitCode."
    }

    if ($NonInteractive) {
        Write-Warning "Running with -NonInteractive: skipping the 'type SAVED to continue' confirmation. Make sure the recovery material printed above has been copied to a safe, offline location before relying on this host."
    } else {
        $confirmation = Read-Host 'Type SAVED to confirm the recovery material above has been copied to a safe, offline location'
        if ($confirmation -ne 'SAVED') {
            throw "Recovery material was not confirmed as saved; aborting before the service is registered. The output file is still at $outFile."
        }
    }

    if (Test-Path $outFile) { Remove-Item -Path $outFile -Force }
}

# ---------------------------------------------------------------------------
# Step 5: register and start the Windows service.
# ---------------------------------------------------------------------------
Write-Host "Step 5: Register and start the '$ServiceName' Windows service."
if (-not $WhatIfMode) {
    $exePath = Join-Path $InstallDir 'minivault.exe'
    $binPath = "`"$exePath`""

    $scCreateArgs = @('create', $ServiceName, 'binPath=', $binPath, 'start=', 'auto', 'obj=', $ServiceAccount)
    if ($serviceAccountNeedsPassword -and -not [string]::IsNullOrWhiteSpace($ServiceAccountPassword)) {
        $scCreateArgs += @('password=', $ServiceAccountPassword)
    }
    & sc.exe @scCreateArgs | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe create failed for service '$ServiceName' (exit code $LASTEXITCODE)."
    }

    & sc.exe description $ServiceName 'Karmasis MiniVault secret vault server.' | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

    & sc.exe start $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe start failed for service '$ServiceName' (exit code $LASTEXITCODE)."
    }
}

# ---------------------------------------------------------------------------
# Step 6: wait for the health endpoint.
# ---------------------------------------------------------------------------
$healthUrl = "https://localhost:$port/v1/health"
Write-Host "Step 6: Wait up to 30 seconds for $healthUrl."
if (-not $WhatIfMode) {
    $deadline = (Get-Date).AddSeconds(30)
    $healthy = $false
    $lastError = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $healthUrl -SkipCertificateCheck -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200) { $healthy = $true; break }
        } catch {
            $lastError = $_
        }
        Start-Sleep -Seconds 2
    }
    if ($healthy) {
        Write-Host "Health check succeeded: $healthUrl responded 200 OK."
    } else {
        Write-Warning "Health check did not succeed within 30 seconds: $healthUrl. Last error: $lastError"
    }
}

if ($WhatIfMode) {
    Write-Host ''
    Write-Host 'WhatIfMode: no changes were made.'
    exit 0
}

$loginName = if ($ServiceAccount -eq 'LocalSystem') { 'NT AUTHORITY\SYSTEM' } else { $ServiceAccount }
$sqlScript = @"
-- Run on the target SQL Server instance so the service account can reach the MiniVault database (Windows Authentication):
CREATE LOGIN [$loginName] FROM WINDOWS;
CREATE USER [$loginName] FOR LOGIN [$loginName];
ALTER ROLE db_owner ADD MEMBER [$loginName];
"@
Write-Host ''
Write-Host 'Reminder: grant the service account access to the MiniVault database before the service can start successfully:'
Write-Host $sqlScript
Write-Host ''
Write-Host "MiniVault installed and service '$ServiceName' registered."
exit 0
