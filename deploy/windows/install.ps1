#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the Karmasis MiniVault server as a Windows service.

.DESCRIPTION
    1. Stops the service if it is already installed, copies the publish output into the install
       directory and creates the ProgramData folder.
    2. Writes the machine-wide configuration file (connection string, master key provider, TLS).
    3. Locks down the ProgramData folder with a protected ACL (well-known SIDs, not localized names).
    4. Runs 'minivault.exe init' and requires the operator to confirm the recovery material is saved.
    5. Prints the SQL grant script, then registers or reconfigures (and, unless -SkipServiceStart,
       starts) the service.
    6. Waits for the health endpoint to respond.

    Re-runnable: when the service already exists it is stopped before Step 1 and reconfigured in
    Step 5 instead of being created, so the same command line upgrades an existing install.

    Runs on Windows PowerShell 5.1 and later. Must be run from an elevated (Administrator) PowerShell
    session, except with -WhatIfMode, which prints the plan and exits without making any changes (and
    does not require elevation).

    Exit codes: 0 success, 1 bad input or a failed step, 2 the service was installed and started but
    the health endpoint did not answer within 30 seconds (see -IgnoreHealthCheck).

.EXAMPLE
    .\install.ps1 -SourceDir C:\publish\minivault -ConnectionString "Server=sql01;Database=MiniVault;Integrated Security=true" -CertificatePath C:\certs\minivault.pfx -CertificatePassword (Read-Host)

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

    # Register the service but do not start it, so the SQL grant printed before Step 5 can be applied
    # first. Start it afterwards with: sc.exe start <ServiceName>
    [switch]$SkipServiceStart,

    # Skip Step 4 ('minivault.exe init') entirely. Use this when restoring an existing vault onto a new
    # host: install everything else (files, config, ACLs, service), then run 'minivault.exe recover' with
    # the recovery material instead of creating a brand-new vault.
    [switch]$SkipInit,

    # Treat a failed health check as a warning (exit 0) instead of exiting 2. For hosts where the
    # SQL grant is applied after the install, or where the service legitimately starts slowly.
    [switch]$IgnoreHealthCheck,

    # Do not grant SeServiceLogonRight ("Log on as a service") to a non-built-in -ServiceAccount.
    # Use this when the right is already granted through Group Policy, which would overwrite a local
    # grant at the next refresh anyway.
    [switch]$SkipLogonRightGrant,

    # Print the install plan and exit 0 without making any changes. Does not require elevation.
    [switch]$WhatIfMode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

<#
Runs a native executable and returns its exit code plus both captured streams. Native commands do not
honour $ErrorActionPreference = 'Stop', and piping their stderr through `2>&1` under 'Stop' turns every
stderr line into a NativeCommandError, so the process is launched detached with both streams redirected
to temp files instead. The temp files are always removed.
#>
function Invoke-NativeProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),

        # Environment variables to set for the child. Windows PowerShell 5.1 has no per-process
        # environment on Start-Process, so they are set on this process (which the child inherits)
        # and removed again in the finally below. This is how a secret reaches the child without
        # ever appearing on a command line.
        [hashtable]$Environment = @{}
    )

    $stdOutFile = [System.IO.Path]::GetTempFileName()
    $stdErrFile = [System.IO.Path]::GetTempFileName()
    foreach ($name in $Environment.Keys) {
        Set-Item -Path "Env:$name" -Value $Environment[$name]
    }
    try {
        # Start-Process joins the array with spaces, so anything containing whitespace must be quoted.
        $quoted = @($ArgumentList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
        $process = Start-Process -FilePath $FilePath -ArgumentList $quoted -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $stdOutFile -RedirectStandardError $stdErrFile

        $stdOut = ''
        $stdErr = ''
        if (Test-Path $stdOutFile) { $stdOut = [string](Get-Content -Path $stdOutFile -Raw) }
        if (Test-Path $stdErrFile) { $stdErr = [string](Get-Content -Path $stdErrFile -Raw) }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut   = $stdOut
            StdErr   = $stdErr
        }
    } finally {
        foreach ($name in $Environment.Keys) {
            Remove-Item -Path "Env:$name" -ErrorAction SilentlyContinue
        }
        Remove-Item -Path $stdOutFile, $stdErrFile -Force -ErrorAction SilentlyContinue
    }
}

<#
Polls the health endpoint until it answers 200 or the deadline passes. Invoke-WebRequest -SkipCertificateCheck
does not exist on Windows PowerShell 5.1, so this uses HttpClient with a permissive certificate callback
(the installer talks to localhost over a certificate that is very often self-signed at this point).
#>
function Wait-ForHealthEndpoint {
    param(
        [Parameter(Mandatory = $true)][string]$HealthUrl,
        [int]$TimeoutSeconds = 30,
        [int]$AttemptTimeoutSeconds = 5
    )

    if ($PSVersionTable.PSVersion.Major -lt 6) { Add-Type -AssemblyName System.Net.Http }

    # Windows PowerShell 5.1 still defaults ServicePointManager to SSL3/TLS1.0 on some hosts, which
    # Kestrel refuses; without this the probe fails with "An existing connection was forcibly closed"
    # against a perfectly healthy server.
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.ServerCertificateCustomValidationCallback = { $true }
    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        $client.Timeout = [TimeSpan]::FromSeconds($AttemptTimeoutSeconds)
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $lastError = 'no attempt completed'
        while ((Get-Date) -lt $deadline) {
            try {
                $response = $client.GetAsync($HealthUrl).GetAwaiter().GetResult()
                try {
                    if ([int]$response.StatusCode -eq 200) {
                        return [pscustomobject]@{ Healthy = $true; LastError = $null }
                    }
                    $lastError = "HTTP $([int]$response.StatusCode)"
                } finally {
                    $response.Dispose()
                }
            } catch {
                $lastError = $_.Exception.GetBaseException().Message
            }
            Start-Sleep -Seconds 2
        }
        return [pscustomobject]@{ Healthy = $false; LastError = $lastError }
    } finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

<#
Grants SeServiceLogonRight ("Log on as a service") to $AccountName. secedit is the only in-box way to
edit a user rights assignment: export the current USER_RIGHTS area, append the account's SID to the
SeServiceLogonRight line, and import the result. Returns $true when the right is already there or was
granted. Without it, Service Control Manager refuses to start the service with error 1069
("The service did not start due to a logon failure").
#>
function Grant-ServiceLogonRight {
    param([Parameter(Mandatory = $true)][string]$AccountName)

    $sid = $null
    try {
        $sid = ([Security.Principal.NTAccount]$AccountName).Translate([Security.Principal.SecurityIdentifier]).Value
    } catch {
        Write-Warning "Could not resolve '$AccountName' to a SID, so SeServiceLogonRight was not granted: $($_.Exception.Message)"
        return $false
    }

    $temp = [System.IO.Path]::GetTempPath()
    $stem = 'minivault-secedit-' + [Guid]::NewGuid().ToString('N')
    $exportPath = Join-Path $temp "$stem-export.inf"
    $importPath = Join-Path $temp "$stem-import.inf"
    $databasePath = Join-Path $temp "$stem.sdb"
    try {
        & secedit.exe /export /areas USER_RIGHTS /cfg $exportPath /quiet | Out-Null
        if (-not (Test-Path $exportPath)) {
            Write-Warning 'secedit /export produced no file, so SeServiceLogonRight was not granted.'
            return $false
        }

        $existing = @(Get-Content -Path $exportPath | Where-Object { $_ -like 'SeServiceLogonRight*' })
        $accounts = ''
        if ($existing.Count -gt 0) {
            $accounts = ($existing[0] -split '=', 2)[1].Trim()
            if ($accounts -split ',' | Where-Object { $_.Trim().TrimStart('*') -eq $sid }) {
                Write-Host "  SeServiceLogonRight is already granted to '$AccountName'."
                return $true
            }
        }
        $accounts = if ([string]::IsNullOrWhiteSpace($accounts)) { "*$sid" } else { "$accounts,*$sid" }

        @(
            '[Unicode]',
            'Unicode=yes',
            '[Version]',
            'signature="$CHICAGO$"',
            'Revision=1',
            '[Privilege Rights]',
            "SeServiceLogonRight = $accounts"
        ) | Set-Content -Path $importPath -Encoding Unicode

        & secedit.exe /configure /db $databasePath /cfg $importPath /areas USER_RIGHTS /quiet | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "secedit /configure returned exit code $LASTEXITCODE; SeServiceLogonRight may not have been granted to '$AccountName'. Grant it manually with secpol.msc (Local Policies > User Rights Assignment > Log on as a service)."
            return $false
        }
        Write-Host "  Granted SeServiceLogonRight to '$AccountName' ($sid)."
        return $true
    } finally {
        Remove-Item -Path $exportPath, $importPath, $databasePath -Force -ErrorAction SilentlyContinue
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

# ---------------------------------------------------------------------------
# Parameter validation (runs before the elevation check so -WhatIfMode and CI
# smoke tests get a clean non-zero exit + message without needing to elevate).
# ---------------------------------------------------------------------------

# Well-known service identities that need no password. LocalSystem is spelled several ways by sc.exe/services.msc.
$localSystemAccounts    = @('LocalSystem', 'NT AUTHORITY\SYSTEM', '.\LocalSystem')
$networkServiceAccounts = @('NetworkService', 'NT AUTHORITY\NetworkService')
$localServiceAccounts   = @('LocalService', 'NT AUTHORITY\LocalService')
$passwordlessAccounts   = $localSystemAccounts + $networkServiceAccounts + $localServiceAccounts

$isLocalSystem = $localSystemAccounts -contains $ServiceAccount
$serviceAccountNeedsPassword = -not ($passwordlessAccounts -contains $ServiceAccount)

# icacls resolves localized group names, so grant by SID: SYSTEM, BUILTIN\Administrators, and the
# service account when it is not LocalSystem. Read/execute is enough for the service: the config and
# key file are only written by 'init'/'recover', which run as the operator.
$serviceAccountSid = $null
if (-not $isLocalSystem) {
    if ($networkServiceAccounts -contains $ServiceAccount) {
        $serviceAccountSid = '*S-1-5-20'
    } elseif ($localServiceAccounts -contains $ServiceAccount) {
        $serviceAccountSid = '*S-1-5-19'
    } else {
        $serviceAccountSid = $ServiceAccount
    }
}

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

if ($hasCertThumbprint) {
    # certmgr.msc copies the thumbprint with spaces and a leading left-to-right mark; normalize both away.
    $normalizedThumbprint = ($CertificateThumbprint -replace '[\s\u200e\u200f:-]', '').ToUpperInvariant()
    if ($normalizedThumbprint -notmatch '^[0-9A-F]{40}$') {
        $validationErrors.Add("-CertificateThumbprint '$CertificateThumbprint' is not a SHA-1 certificate thumbprint: it must normalize to exactly 40 hexadecimal characters (got $($normalizedThumbprint.Length)).")
    } else {
        $CertificateThumbprint = $normalizedThumbprint
    }
}

# A double quote cannot survive the places these values are re-quoted on their way to a child
# process (sc.exe's "name= value" command line, the MSI's CustomActionData). Reject it up front with a
# message naming the parameter instead of failing halfway through with a truncated value.
foreach ($secret in @(
        @{ Name = '-MasterKeyPassword';      Value = $MasterKeyPassword },
        @{ Name = '-CertificatePassword';    Value = $CertificatePassword },
        @{ Name = '-ServiceAccountPassword'; Value = $ServiceAccountPassword })) {
    if (-not [string]::IsNullOrEmpty($secret.Value) -and $secret.Value.Contains('"')) {
        $validationErrors.Add("$($secret.Name) must not contain a double quote (`"). Choose a value without quotes.")
    }
}

if ($serviceAccountNeedsPassword -and [string]::IsNullOrWhiteSpace($ServiceAccountPassword)) {
    $validationErrors.Add("-ServiceAccountPassword is required when -ServiceAccount ('$ServiceAccount') is not LocalSystem, NetworkService or LocalService.")
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
    # One error record, listing everything that is wrong. Write-Error is terminating here
    # ($ErrorActionPreference = 'Stop'), so writing one record per problem would print only the first
    # and skip the 'exit 1' below; -ErrorAction Continue keeps both the full list and the exit code.
    $lines = @($validationErrors | ForEach-Object { "  - $_" })
    Write-Error ("install.ps1 cannot run:" + [Environment]::NewLine + ($lines -join [Environment]::NewLine)) -ErrorAction Continue
    exit 1
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

# Re-running the installer over an existing install is the normal upgrade path: the service is stopped
# before its files are replaced (robocopy /MIR cannot overwrite a running binary) and reconfigured
# instead of created. Get-Service needs no elevation, so -WhatIfMode reports this too.
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$serviceExists = $null -ne $existingService
$existingServiceNote = if ($serviceExists) { ' (service exists -> stop/config)' } else { '' }

Write-Host "MiniVault install plan"
Write-Host "  Service name  : $ServiceName"
Write-Host "  Install dir   : $InstallDir"
Write-Host "  Source dir    : $SourceDir"
Write-Host "  ProgramData   : $programDataDir"
Write-Host "  Service acct  : $ServiceAccount"
Write-Host "  Existing svc  : $(if ($serviceExists) { "yes, '$ServiceName' will be stopped and reconfigured" } else { 'no, it will be created' })"
Write-Host "  URL           : $Url"
Write-Host ''

if ($hasCertPath -and -not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
    Write-Host "Warning: the PFX password is written in plain text to $machineConfigPath (Tls:Certificate:Password). That file is ACL-protected in Step 3 to SYSTEM, Administrators and the service account, but it is still a secret at rest on this host. Importing the certificate into LocalMachine\My and using -CertificateThumbprint avoids storing it." -ForegroundColor Yellow
    Write-Host ''
}

# ---------------------------------------------------------------------------
# Step 1: copy the publish output, create the ProgramData folder.
# ---------------------------------------------------------------------------
Write-Host "Step 1: Stop '$ServiceName' if it is running$existingServiceNote, copy '$SourceDir' to '$InstallDir' (robocopy /MIR) and create '$programDataDir'."
if (-not $WhatIfMode) {
    if ($serviceExists) {
        # robocopy /MIR cannot replace a running executable, so the service goes down first.
        Write-Host "  Stopping '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) {
            $current = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if ($null -eq $current -or $current.Status -eq 'Stopped') { break }
            Start-Sleep -Seconds 1
        }
        $current = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -ne $current -and $current.Status -ne 'Stopped') {
            throw "Service '$ServiceName' did not stop within 30 seconds (status: $($current.Status)); its files cannot be replaced while it runs."
        }
    }

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

    # Set-Content -Encoding utf8 writes a BOM on Windows PowerShell 5.1, and .NET's JSON configuration
    # provider rejects a file that starts with one. Write UTF-8 without a BOM instead.
    [IO.File]::WriteAllText($machineConfigPath, ($config | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
}

# ---------------------------------------------------------------------------
# Step 3: lock down %ProgramData%\MiniVault.
# ---------------------------------------------------------------------------
$grants = @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F')
if ($null -ne $serviceAccountSid) { $grants += "$($serviceAccountSid):(OI)(CI)RX" }
Write-Host "Step 3: Apply a protected ACL to $programDataDir (icacls /inheritance:r /grant:r $($grants -join ' '))."
if (-not $WhatIfMode) {
    & icacls.exe $programDataDir /inheritance:r /grant:r @grants | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "icacls failed to protect '$programDataDir' (exit code $LASTEXITCODE)."
    }
}

# ---------------------------------------------------------------------------
# Step 4: initialize the vault.
# ---------------------------------------------------------------------------
if ($SkipInit) {
    Write-Host "Step 4: Skipped (-SkipInit). Note: this host has no vault yet - after the service is installed, run 'minivault.exe recover --new-master-key auto --share ... --share ...' (or --recovery-key) with your existing recovery material to restore it, before starting the service."
} else {
    Write-Host "Step 4: Run 'minivault.exe init --recovery $Recovery' and confirm the recovery material has been saved."
}
if (-not $WhatIfMode -and -not $SkipInit) {
    $exePath = Join-Path $InstallDir 'minivault.exe'
    $timestamp = Get-Date -Format 'yyyyMMddHHmmss'
    $outFile = Join-Path $programDataDir "recovery-$timestamp.txt"

    $initArgs = @('init', '--recovery', $Recovery, '--out', $outFile)
    if ($Recovery -eq 'shamir') { $initArgs += @('--shares', "$Shares", '--threshold', "$Threshold") }
    # The password is handed to the child through MINIVAULT_INIT_MASTER_KEY, never as
    # '--master-key <password>': a command line is readable by anything that can list processes and is
    # captured by command-line auditing (Event ID 4688). minivault.exe clears the variable from its own
    # environment as soon as it has read it.
    $initEnvironment = @{}
    if (-not [string]::IsNullOrWhiteSpace($MasterKeyPassword)) {
        $initArgs += '--master-key-from-env'
        $initEnvironment['MINIVAULT_INIT_MASTER_KEY'] = $MasterKeyPassword
    }

    $init = Invoke-NativeProcess -FilePath $exePath -ArgumentList $initArgs -Environment $initEnvironment
    if (-not [string]::IsNullOrWhiteSpace($init.StdOut)) { Write-Host $init.StdOut }
    if ($init.ExitCode -ne 0) {
        throw "minivault.exe init failed with exit code $($init.ExitCode). $($init.StdErr)".Trim()
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
# The SQL grant comes before the service is created/started on purpose: the service cannot reach the
# database without it, so the operator gets the script while there is still time to run it (and can
# combine it with -SkipServiceStart).
# ---------------------------------------------------------------------------
$loginName = if ($isLocalSystem) { 'NT AUTHORITY\SYSTEM' } else { $ServiceAccount }
$sqlScript = @"
-- Run on the target SQL Server instance, in the MiniVault database (Windows Authentication).
-- The RUNNING SERVICE only reads and writes rows: it never changes the schema, so db_datareader +
-- db_datawriter is all it needs. Do not give it db_owner.
CREATE LOGIN [$loginName] FROM WINDOWS;
CREATE USER  [$loginName] FOR LOGIN [$loginName];
ALTER ROLE db_datareader ADD MEMBER [$loginName];
ALTER ROLE db_datawriter ADD MEMBER [$loginName];
"@
$sqlDdlScript = @"
-- Separately: 'minivault.exe init' and 'minivault.exe migrate' create and alter tables, and they run
-- as the OPERATOR (this PowerShell session), not as the service. That account needs DDL rights on the
-- database - and, if the database does not exist yet, permission to create it:
--   ALTER ROLE db_ddladmin ADD MEMBER [DOMAIN\operator];   -- enough for migrate on an existing schema
--   ALTER ROLE db_owner    ADD MEMBER [DOMAIN\operator];   -- needed the first time (init creates the schema)
-- Revoke them again once the upgrade is done if your policy requires least privilege at rest.
"@
Write-Host ''
Write-Host 'Grant the service account access to the MiniVault database before the service can start successfully:'
Write-Host $sqlScript
Write-Host $sqlDdlScript
Write-Host ''

# ---------------------------------------------------------------------------
# Step 5: register (and start) the Windows service.
# ---------------------------------------------------------------------------
$registerVerb = if ($serviceExists) { 'reconfigure' } else { 'register' }
$startDescription = if ($SkipServiceStart) { "$registerVerb (not start, -SkipServiceStart)" } else { "$registerVerb and start" }
Write-Host "Step 5: $startDescription the '$ServiceName' Windows service$existingServiceNote."
if ($serviceAccountNeedsPassword -and -not $SkipLogonRightGrant) {
    Write-Host "        Grant SeServiceLogonRight ('Log on as a service') to '$ServiceAccount' (secedit; pass -SkipLogonRightGrant to skip)."
}
if (-not $WhatIfMode) {
    $exePath = Join-Path $InstallDir 'minivault.exe'
    $binPath = "`"$exePath`""

    if ($serviceExists) {
        # sc.exe config, not create: keep the existing service (and its SID/ACLs) and point it at the
        # new binary. 'password=' has to go on the command line here - sc.exe has no other way to set
        # it - which is why the warning below is printed.
        $scConfigArgs = @('config', $ServiceName, 'binPath=', $binPath, 'start=', 'auto', 'obj=', $ServiceAccount)
        if ($serviceAccountNeedsPassword) {
            Write-Warning "The service account password is passed to 'sc.exe config password= ...' on its command line, where anything that can list processes - and command-line auditing (Event ID 4688) - can read it. Nothing in Windows offers a password-free reconfigure; if that is unacceptable, delete the service and let this script create it (New-Service -Credential keeps the password out of the command line)."
            $scConfigArgs += @('password=', $ServiceAccountPassword)
        }
        $scOutput = & sc.exe @scConfigArgs
        if ($LASTEXITCODE -ne 0) {
            throw "sc.exe config failed for service '$ServiceName' (exit code $LASTEXITCODE): $($scOutput -join ' ')"
        }
    } elseif ($serviceAccountNeedsPassword) {
        # New-Service -Credential passes the password through the Win32 API, so it never reaches a
        # command line. sc.exe create would have had to take 'password= <secret>' as an argument.
        $securePassword = ConvertTo-SecureString $ServiceAccountPassword -AsPlainText -Force
        $credential = New-Object System.Management.Automation.PSCredential($ServiceAccount, $securePassword)
        New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName $ServiceName `
            -Description 'Karmasis MiniVault secret vault server.' -StartupType Automatic -Credential $credential | Out-Null
    } else {
        $scCreateArgs = @('create', $ServiceName, 'binPath=', $binPath, 'start=', 'auto', 'obj=', $ServiceAccount)
        $scOutput = & sc.exe @scCreateArgs
        if ($LASTEXITCODE -ne 0) {
            throw "sc.exe create failed for service '$ServiceName' (exit code $LASTEXITCODE): $($scOutput -join ' ')"
        }
    }

    $scOutput = & sc.exe description $ServiceName 'Karmasis MiniVault secret vault server.'
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe description failed for service '$ServiceName' (exit code $LASTEXITCODE): $($scOutput -join ' ')"
    }

    $scOutput = & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe failure failed for service '$ServiceName' (exit code $LASTEXITCODE): $($scOutput -join ' ')"
    }

    # Without "Log on as a service" the SCM refuses to start the service with error 1069, whether it
    # was just created or only reconfigured.
    if ($serviceAccountNeedsPassword -and -not $SkipLogonRightGrant) {
        Grant-ServiceLogonRight -AccountName $ServiceAccount | Out-Null
    }

    if ($SkipServiceStart) {
        Write-Host "Service '$ServiceName' $(if ($serviceExists) { 'reconfigured' } else { 'created' }) but not started (-SkipServiceStart). Run the SQL grant above, then: sc.exe start $ServiceName"
    } else {
        $scOutput = & sc.exe start $ServiceName
        if ($LASTEXITCODE -ne 0) {
            throw "sc.exe start failed for service '$ServiceName' (exit code $LASTEXITCODE): $($scOutput -join ' ')"
        }
    }
}

# ---------------------------------------------------------------------------
# Step 6: wait for the health endpoint.
# ---------------------------------------------------------------------------
$healthUrl = "https://localhost:$port/v1/health"
if ($SkipServiceStart) {
    Write-Host "Step 6: Skipped - the service was not started. After 'sc.exe start $ServiceName', check $healthUrl."
} else {
    Write-Host "Step 6: Wait up to 30 seconds for $healthUrl. A failed health check exits 2 unless -IgnoreHealthCheck is passed."
}
if (-not $WhatIfMode -and -not $SkipServiceStart) {
    $health = Wait-ForHealthEndpoint -HealthUrl $healthUrl -TimeoutSeconds 30 -AttemptTimeoutSeconds 5
    if ($health.Healthy) {
        Write-Host "Health check succeeded: $healthUrl responded 200 OK."
    } else {
        Write-Warning "Health check did not succeed within 30 seconds: $healthUrl. Last error: $($health.LastError)"
        if ($IgnoreHealthCheck) {
            Write-Warning '-IgnoreHealthCheck was passed, so this is not treated as a failure. Check the service and the Windows event log before relying on this host.'
        } else {
            # A distinct exit code: the install itself completed, so a caller can tell "nothing was
            # installed" (1) from "installed but not serving" (2) - usually a missing SQL grant, a bad
            # certificate, or an uninitialized vault. See docs/operations.md, Troubleshooting.
            Write-Error "MiniVault was installed but $healthUrl did not answer. Check 'sc.exe query $ServiceName', the Windows Application event log, and the SQL grant printed above. Re-run with -IgnoreHealthCheck to ignore this." -ErrorAction Continue
            exit 2
        }
    }
}

if ($WhatIfMode) {
    Write-Host ''
    Write-Host 'WhatIfMode: no changes were made.'
    exit 0
}

Write-Host ''
Write-Host "MiniVault installed and service '$ServiceName' registered."
exit 0
