#Requires -Version 5.1
<#
.SYNOPSIS
    Static checks on Karmasis.MiniVault.aip, plus (with -Build) a real build through the Advanced
    Installer command line when the product is installed on this machine.

.DESCRIPTION
    1. The .aip parses as XML and has the components the setup relies on.
    2. Lists every custom action row with its binary, type and sequencing, and checks that each
       managed custom action names a method that exists in the custom-actions assembly's source.
    3. Every MsiFilesComponent SourcePath and the SynchronizedFolderComponent SourcePath resolve to
       something that exists (run 'dotnet publish src/Karmasis.MiniVault.Server -p:PublishProfile=win-x64'
       first, or pass -SkipPayload).
    4. The custom-actions assembly paths referenced from the .aip match the project's output path,
       and (unless -SkipPayload) the built DLL is there.
    5. Every secret is hidden from the MSI log - the MV_* properties an operator types AND the
       CustomActionData property of every deferred managed custom action, which carries copies of
       them - the properties the custom actions read exist, no Property row has an empty Value
       (Advanced Installer refuses to open the project otherwise), and the service is actually
       started on install.
    6. The configuration dialogs: declared, with their chrome, unique control keys, bound properties
       known, password edits hidden, check boxes in the CheckBox table, events resolving to known
       dialogs/actions/properties, the page navigation both ways, the SQL test button sequence.
    7. (-Build only) Copies the project to <name>.check.aip next to it and runs
       'AdvancedInstaller.com /build' on the copy, so the real loader and validator get their say
       without the designer touching the tracked file. Loader errors (schema, missing rows) fail the
       check; AI_ICE validation lines are printed but do not fail it, because Advanced Installer
       itself still produces the MSI (exit code 0) - AI_ICE07 in particular fires for every edit
       control bound to a property without a default, which is every secret here by design.
       The MSI lands in 'Setup Files\' next to the project (git-ignored), ready for a test install.

    Exits 0 when everything checks out, 1 otherwise.

.EXAMPLE
    .\setups\AdvancedInstaller\verify-aip.ps1

.EXAMPLE
    .\setups\AdvancedInstaller\verify-aip.ps1 -SkipPayload

.EXAMPLE
    .\setups\AdvancedInstaller\verify-aip.ps1 -Build
#>
[CmdletBinding()]
param(
    [string]$AipPath,

    # Skip the checks that need 'dotnet publish' / a built custom-actions assembly.
    [switch]$SkipPayload,

    # Also build the MSI with AdvancedInstaller.com (needs Advanced Installer on this machine).
    [switch]$Build,

    # AdvancedInstaller.com to use for -Build; found under %ProgramFiles(x86)%\Caphyon when omitted.
    [string]$AdvancedInstallerCom
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$setupRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($AipPath)) {
    $AipPath = Join-Path $setupRoot 'Karmasis.MiniVault\Karmasis.MiniVault.aip'
}

$script:failures = [System.Collections.Generic.List[string]]::new()

function Write-Section { param([string]$Title) Write-Host ''; Write-Host "== $Title" -ForegroundColor Cyan }
function Write-Ok      { param([string]$Message) Write-Host "  [ok]   $Message" -ForegroundColor Green }
function Write-Fail    { param([string]$Message) Write-Host "  [FAIL] $Message" -ForegroundColor Red; $script:failures.Add($Message) }
function Write-Skip    { param([string]$Message) Write-Host "  [skip] $Message" -ForegroundColor DarkGray }

# ---------------------------------------------------------------------------
# 1. Well-formed XML and expected components
# ---------------------------------------------------------------------------
Write-Section "1. $([System.IO.Path]::GetFileName($AipPath)) is well-formed"

if (-not (Test-Path -LiteralPath $AipPath)) {
    Write-Fail "Not found: $AipPath"
    exit 1
}

try {
    $aip = [xml](Get-Content -LiteralPath $AipPath -Raw)
    Write-Ok "parsed as XML ($([math]::Round((Get-Item $AipPath).Length / 1KB, 1)) KB)"
} catch {
    Write-Fail "not well-formed XML: $($_.Exception.Message)"
    exit 1
}

$components = @{}
foreach ($component in $aip.DOCUMENT.COMPONENT) {
    $components[$component.cid] = $component
}

$requiredComponents = @(
    'caphyon.advinst.msicomp.MsiPropsComponent',
    'caphyon.advinst.msicomp.MsiDirsComponent',
    'caphyon.advinst.msicomp.MsiCompsComponent',
    'caphyon.advinst.msicomp.MsiFilesComponent',
    'caphyon.advinst.msicomp.MsiCreateFolderComponent',
    'caphyon.advinst.msicomp.MsiLockPermComponent',
    'caphyon.advinst.msicomp.MsiServInstComponent',
    'caphyon.advinst.msicomp.MsiServCtrlComponent',
    'caphyon.advinst.msicomp.MsiCustActComponent',
    'caphyon.advinst.msicomp.MsiInstExSeqComponent',
    'caphyon.advinst.msicomp.TempFileComponent',
    'caphyon.advinst.msicomp.SynchronizedFolderComponent'
)
foreach ($cid in $requiredComponents) {
    if ($components.ContainsKey($cid)) {
        Write-Ok $cid.Replace('caphyon.advinst.msicomp.', '')
    } else {
        Write-Fail "missing component: $cid"
    }
}

# A .NET Framework launch condition would be wrong: the server publishes self-contained.
$launchConditions = $components['caphyon.advinst.msicomp.MsiLaunchConditionsComponent']
if ($null -ne $launchConditions) {
    $dotNetCondition = @($launchConditions.ROW | Where-Object { $_.Condition -like '*DOTNET*' })
    if ($dotNetCondition.Count -eq 0) {
        Write-Ok 'no .NET Framework launch condition (self-contained publish)'
    } else {
        Write-Fail "unexpected .NET launch condition: $($dotNetCondition[0].Condition)"
    }
}

# ---------------------------------------------------------------------------
# 2. Custom actions
# ---------------------------------------------------------------------------
Write-Section '2. Custom actions'

$customActions = @($components['caphyon.advinst.msicomp.MsiCustActComponent'].ROW)
$sequenceRows = @{}
if ($components.ContainsKey('caphyon.advinst.msicomp.MsiInstExSeqComponent')) {
    foreach ($row in $components['caphyon.advinst.msicomp.MsiInstExSeqComponent'].ROW) {
        $sequenceRows[$row.Action] = $row
    }
}

# MSI CustomAction type bits, for the human-readable summary below.
function Get-CustomActionKind {
    param([int]$Type)
    $bits = [System.Collections.Generic.List[string]]::new()
    switch ($Type -band 0x3F) {
        1  { $bits.Add('binary-dll') }
        2  { $bits.Add('binary-exe') }
        19 { $bits.Add('error') }
        51 { $bits.Add('property-set') }
        35 { $bits.Add('directory-set') }
        default { $bits.Add("source-type-$($Type -band 0x3F)") }
    }
    if ($Type -band 0x400) { $bits.Add('deferred') } else { $bits.Add('immediate') }
    if ($Type -band 0x800) { $bits.Add('no-impersonate') }
    if ($Type -band 0x40)  { $bits.Add('ignore-return') }
    if ($Type -band 0x100) { $bits.Add('first-sequence-only') }
    return ($bits -join ', ')
}

$managedActions = [ordered]@{}
foreach ($row in $customActions) {
    $name = $row.Action
    $type = [int]$row.Type
    $source = if ($row.PSObject.Properties.Name -contains 'Source') { $row.Source } else { '' }
    $target = if ($row.PSObject.Properties.Name -contains 'Target') { $row.Target } else { '' }

    $sequence = if ($sequenceRows.ContainsKey($name)) { $sequenceRows[$name].Sequence } else { '-' }
    $line = '  {0,-28} type={1,-5} binary={2,-24} seq={3,-5} ({4})' -f `
        $name, $type, $source, $sequence, (Get-CustomActionKind -Type $type)
    Write-Host $line

    # 'args;|[&fileid]|Namespace.Type.Method' - the DotNetMethodCaller payload.
    if ($target -match '\|\[&(?<fileId>[^\]]+)\]\|(?<method>[\w\.]+)$') {
        $managedActions[$name] = [pscustomobject]@{
            SetterAction = $name
            FileId       = $Matches['fileId']
            Method       = $Matches['method']
        }
    }
}

if ($managedActions.Count -eq 0) {
    Write-Fail 'no managed (DotNetMethodCaller) custom actions found'
} else {
    Write-Ok "$($managedActions.Count) managed custom action target(s) declared"
}

$expectedMethods = @(
    'Karmasis.MiniVault.CustomActions.InstallActions.WriteMachineConfig',
    'Karmasis.MiniVault.CustomActions.InstallActions.RunInit',
    'Karmasis.MiniVault.CustomActions.InstallActions.TestSqlConnection',
    'Karmasis.MiniVault.CustomActions.InstallActions.ValidateProperties'
)
$declaredMethods = @($managedActions.Values | ForEach-Object { $_.Method })
foreach ($method in $expectedMethods) {
    if ($declaredMethods -contains $method) {
        Write-Ok "declared: $method"
    } else {
        Write-Fail "custom action method not declared in the .aip: $method"
    }
}

# Every deferred MiniVault action must have a property setter named exactly after it, or MSI hands
# it no data. Only the managed ones are checked; Advanced Installer's own deferred actions
# (AI_DoRemoveExternalUIStub and friends) get their data through other mechanisms.
foreach ($row in $customActions | Where-Object {
        $_.PSObject.Properties.Name -contains 'Source' -and $_.Source -eq 'DotNetMethodCaller.dll' }) {
    $type = [int]$row.Type
    if (($type -band 0x400) -and (($type -band 0x3F) -eq 1)) {
        $setter = @($customActions | Where-Object { [int]$_.Type -eq 51 -and $_.Source -eq $row.Action })
        if ($setter.Count -eq 1) {
            Write-Ok "deferred '$($row.Action)' gets its CustomActionData from '$($setter[0].Action)'"
        } else {
            Write-Fail "deferred '$($row.Action)' has $($setter.Count) property setters named after it (expected exactly 1)"
        }
    }
}

# ---------------------------------------------------------------------------
# 2b. Managed custom actions must run after the payload is extracted
# ---------------------------------------------------------------------------
Write-Section '2b. Managed custom actions run after AI_ExtractTempFiles'

# "Managed" here means: the property setters that stage a managed action's CustomActionData
# (already collected in $managedActions, keyed by setter name) and the DotNetMethodCaller.dll rows
# that actually invoke the custom-actions DLL. Both need the DLL to already be on disk, which
# AI_ExtractTempFiles is what puts it there.
$managedActionNames = [System.Collections.Generic.HashSet[string]]::new()
foreach ($name in $managedActions.Keys) { [void]$managedActionNames.Add($name) }
foreach ($row in $customActions | Where-Object {
        $_.PSObject.Properties.Name -contains 'Source' -and $_.Source -eq 'DotNetMethodCaller.dll' }) {
    [void]$managedActionNames.Add($row.Action)
}

$sequenceTableComponents = @($components.Values | Where-Object {
        $_.PSObject.Properties.Name -contains 'ROW' -and (@($_.ROW) | Where-Object {
                $_.PSObject.Properties.Name -contains 'Action' -and $_.PSObject.Properties.Name -contains 'Sequence'
            })
    })

foreach ($table in $sequenceTableComponents) {
    $tableName = $table.cid.Replace('caphyon.advinst.msicomp.', '')
    $seqByAction = @{}
    foreach ($row in @($table.ROW)) {
        if ($row.PSObject.Properties.Name -contains 'Action' -and $row.PSObject.Properties.Name -contains 'Sequence') {
            $seqByAction[$row.Action] = [int]$row.Sequence
        }
    }
    if (-not $seqByAction.ContainsKey('AI_ExtractTempFiles')) { continue }
    $extractSeq = $seqByAction['AI_ExtractTempFiles']

    foreach ($name in $managedActionNames) {
        if (-not $seqByAction.ContainsKey($name)) { continue }
        $seq = $seqByAction[$name]
        if ($seq -gt $extractSeq) {
            Write-Ok "${tableName}: '$name' (seq=$seq) runs after AI_ExtractTempFiles (seq=$extractSeq)"
        } else {
            Write-Fail "${tableName}: '$name' (seq=$seq) runs at or before AI_ExtractTempFiles (seq=$extractSeq) - the custom-actions DLL may not be extracted yet"
        }
    }
}

# Custom action method names must exist in the C# source.
$actionsSource = Join-Path $setupRoot 'Karmasis.MiniVault.CustomActions\InstallActions.cs'
if (Test-Path -LiteralPath $actionsSource) {
    $sourceText = Get-Content -LiteralPath $actionsSource -Raw
    foreach ($method in $declaredMethods) {
        $shortName = $method.Split('.')[-1]
        if ($sourceText -match "public static int\s+$([regex]::Escape($shortName))\s*\(\s*string\s") {
            Write-Ok "entry point exists: $shortName(string sessionHandle)"
        } else {
            Write-Fail "no 'public static int $shortName(string sessionHandle)' in InstallActions.cs"
        }
    }
} else {
    Write-Fail "not found: $actionsSource"
}

# ---------------------------------------------------------------------------
# 3. Payload paths
# ---------------------------------------------------------------------------
Write-Section '3. Payload paths'

$aipDir = Split-Path -Parent (Resolve-Path -LiteralPath $AipPath)

function Resolve-AipPath {
    param([string]$RelativePath)
    return [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($aipDir, $RelativePath))
}

foreach ($row in @($components['caphyon.advinst.msicomp.MsiFilesComponent'].ROW)) {
    $full = Resolve-AipPath $row.SourcePath
    if ($SkipPayload) {
        Write-Skip "MsiFilesComponent '$($row.File)' -> $full"
    } elseif (Test-Path -LiteralPath $full -PathType Leaf) {
        Write-Ok "MsiFilesComponent '$($row.File)' -> $full"
    } else {
        Write-Fail "MsiFilesComponent '$($row.File)' points at a missing file: $full (run: dotnet publish src/Karmasis.MiniVault.Server -p:PublishProfile=win-x64)"
    }
}

foreach ($row in @($components['caphyon.advinst.msicomp.SynchronizedFolderComponent'].ROW)) {
    $full = Resolve-AipPath $row.SourcePath
    if ($SkipPayload) {
        Write-Skip "synchronized folder -> $full"
    } elseif (Test-Path -LiteralPath $full -PathType Container) {
        $count = @(Get-ChildItem -LiteralPath $full -File).Count
        if ($count -eq 0) {
            Write-Fail "synchronized folder is empty: $full"
        } elseif (-not (Test-Path -LiteralPath (Join-Path $full 'minivault.exe'))) {
            Write-Fail "synchronized folder has no minivault.exe: $full"
        } else {
            Write-Ok "synchronized folder -> $full ($count files, minivault.exe present)"
        }
    } else {
        Write-Fail "synchronized folder is missing: $full (run: dotnet publish src/Karmasis.MiniVault.Server -p:PublishProfile=win-x64)"
    }
}

# The images the dialogs bind to.
foreach ($row in @($components['caphyon.advinst.msicomp.MsiBinaryComponent'].ROW | Where-Object { $_.SourcePath -notlike '<AI_CUSTACTS>*' })) {
    $full = Resolve-AipPath $row.SourcePath
    if (Test-Path -LiteralPath $full -PathType Leaf) {
        Write-Ok "binary '$($row.Name)' -> $full"
    } else {
        Write-Fail "binary '$($row.Name)' points at a missing file: $full"
    }
}

# ---------------------------------------------------------------------------
# 4. Custom-actions assembly paths
# ---------------------------------------------------------------------------
Write-Section '4. Custom-actions assembly'

# The single source of truth for where the project writes its output.
$customActionsProject = Join-Path $setupRoot 'Karmasis.MiniVault.CustomActions\Karmasis.MiniVault.CustomActions.csproj'
if (-not (Test-Path -LiteralPath $customActionsProject)) {
    Write-Fail "not found: $customActionsProject"
} else {
    $projectXml = [xml](Get-Content -LiteralPath $customActionsProject -Raw)
    # local-name() so the lookup works for both an SDK-style project (no namespace) and a classic
    # one (the 2003 msbuild namespace).
    $outputPaths = @($projectXml.SelectNodes("//*[local-name()='OutputPath']") |
        ForEach-Object { $_.InnerText } | Select-Object -Unique)
    if ($outputPaths.Count -eq 0) {
        # No explicit OutputPath: the SDK default (bin\<Configuration>\<TargetFramework>\) would not
        # match the .aip, which references bin\Release\ directly.
        Write-Fail "no <OutputPath> in $customActionsProject; the .aip expects bin\Release\"
        $outputPaths = @('bin\Release\')
    }
    $assemblyNameNode = $projectXml.SelectSingleNode("//*[local-name()='AssemblyName']")
    $assemblyName = if ($null -ne $assemblyNameNode) { $assemblyNameNode.InnerText } else { 'Karmasis.MiniVault.CustomActions' }
    Write-Ok "project output path(s): $($outputPaths -join ', ') (assembly: $assemblyName)"

    $projectDirectory = Split-Path -Parent $customActionsProject
    $tempFileRows = @($components['caphyon.advinst.msicomp.TempFileComponent'].ROW)
    $mainAssemblyRow = @($tempFileRows | Where-Object { $_.Data -like "*$assemblyName.dll" })

    if ($mainAssemblyRow.Count -ne 1) {
        Write-Fail "expected exactly one TempFileComponent row for $assemblyName.dll, found $($mainAssemblyRow.Count)"
    } else {
        $referenced = Resolve-AipPath $mainAssemblyRow[0].Data
        $expected = @($outputPaths | ForEach-Object {
            [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($projectDirectory, $_, "$assemblyName.dll"))
        })

        if ($expected -contains $referenced) {
            Write-Ok "the .aip references the project's own output: $referenced"
        } else {
            Write-Fail "the .aip references '$referenced' but the project builds to '$($expected -join "' or '")'"
        }

        if ($SkipPayload) {
            Write-Skip "built assembly: $referenced"
        } elseif (Test-Path -LiteralPath $referenced -PathType Leaf) {
            Write-Ok "built assembly present: $referenced"
        } else {
            Write-Fail "built assembly missing: $referenced (run 'dotnet build Karmasis.MiniVault.sln -c Release' first)"
        }
    }

    # Every [&fileId] the custom actions reference must be a TempFile row.
    foreach ($action in $managedActions.Values) {
        if (@($tempFileRows | Where-Object { $_.FileId -eq $action.FileId }).Count -eq 1) {
            Write-Ok "custom action '$($action.SetterAction)' resolves [&$($action.FileId)]"
        } else {
            Write-Fail "custom action '$($action.SetterAction)' references unknown temp file id '$($action.FileId)'"
        }
    }

    # The Advanced Installer Kit assembly travels with it or the custom actions cannot load.
    if (@($tempFileRows | Where-Object { $_.Data -like '*Karmasis.AdvancedInstallerKit.dll' }).Count -eq 1) {
        Write-Ok 'Karmasis.AdvancedInstallerKit.dll is deployed alongside the custom actions'
    } else {
        Write-Fail 'no TempFileComponent row for Karmasis.AdvancedInstallerKit.dll'
    }
}

# ---------------------------------------------------------------------------
# 5. Secrets, properties and the service control event
# ---------------------------------------------------------------------------
Write-Section '5. Secrets, properties and the service control event'

$properties = @{}
foreach ($row in @($components['caphyon.advinst.msicomp.MsiPropsComponent'].ROW)) {
    $value = if ($row.PSObject.Properties.Name -contains 'Value') { $row.Value } else { '' }
    $properties[$row.Property] = $value
    # Property.Value is a required column: Advanced Installer refuses to open a project with an empty
    # one ("Required column [Property.Value] has empty value"). A property without a default must
    # have no row at all - an undefined property reads as empty anyway.
    if ([string]::IsNullOrEmpty($value)) {
        Write-Fail "Property row '$($row.Property)' has an empty Value; drop the row (an undefined property reads as empty)"
    }
}

# A property is "known" when it has a Property row, or exists without a default: listed in
# SecureCustomProperties / MsiHiddenProperties, bound through the CheckBox table, or set by a
# control event ([NAME] event). Anything a dialog binds or an action reads must be in this set,
# so a typo in a property name still fails here.
$knownProperties = @{}
foreach ($name in $properties.Keys) { $knownProperties[$name] = $true }
foreach ($listProperty in @('SecureCustomProperties', 'MsiHiddenProperties')) {
    if ($properties.ContainsKey($listProperty)) {
        foreach ($name in @($properties[$listProperty] -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
            $knownProperties[$name] = $true
        }
    }
}
if ($components.ContainsKey('caphyon.advinst.msicomp.MsiCheckBoxComponent')) {
    foreach ($row in @($components['caphyon.advinst.msicomp.MsiCheckBoxComponent'].ROW)) { $knownProperties[$row.Property] = $true }
}
foreach ($listComponent in @('caphyon.advinst.msicomp.MsiRadioButtonComponent', 'caphyon.advinst.msicomp.MsiComboBoxComponent')) {
    if ($components.ContainsKey($listComponent)) {
        foreach ($row in @($components[$listComponent].ROW)) { $knownProperties[$row.Property] = $true }
    }
}
if ($components.ContainsKey('caphyon.advinst.msicomp.MsiControlEventComponent')) {
    foreach ($row in @($components['caphyon.advinst.msicomp.MsiControlEventComponent'].ROW)) {
        if ($row.Event -match '^\[(.+)\]$') { $knownProperties[$Matches[1]] = $true }
    }
}

$hiddenProperties = @()
if ($properties.ContainsKey('MsiHiddenProperties')) {
    $hiddenProperties = @($properties['MsiHiddenProperties'] -split ';' |
        ForEach-Object { $_.Trim() } | Where-Object { $_ })
}
if ($hiddenProperties.Count -eq 0) {
    Write-Fail 'MsiHiddenProperties is missing or empty: msiexec /l*v would log every secret'
}

# The secrets an operator types on the msiexec command line or into a dialog.
foreach ($secret in @('MV_CONNECTIONSTRING', 'MV_CERT_PASSWORD', 'MV_MASTERKEY', 'MV_SERVICEACCOUNT_PASSWORD')) {
    if (-not $knownProperties.ContainsKey($secret)) {
        Write-Fail "property not declared: $secret"
    } elseif ($hiddenProperties -contains $secret) {
        Write-Ok "hidden from the MSI log: $secret"
    } else {
        Write-Fail "$secret is not in MsiHiddenProperties, so msiexec /l*v would write it to the log"
    }
}

# Properties the custom actions read but that carry no secret.
foreach ($property in @('MV_RECONFIGURE', 'MV_SERVICEACCOUNT')) {
    if ($knownProperties.ContainsKey($property)) {
        Write-Ok "property declared: $property"
    } else {
        Write-Fail "property not declared: $property"
    }
}

# A deferred action reads its input from a property named after the action, and MSI logs that
# property like any other. The setter copied the secrets into it, so the action name has to be
# hidden too - hiding only the MV_* properties would leave full copies in the log.
$deferredManagedActions = @($customActions |
    Where-Object { $_.PSObject.Properties.Name -contains 'Source' -and $_.Source -eq 'DotNetMethodCaller.dll' -and ([int]$_.Type -band 0x400) } |
    ForEach-Object { $_.Action })
foreach ($setter in @($customActions | Where-Object { [int]$_.Type -eq 51 })) {
    $target = $setter.Source
    if ($deferredManagedActions -notcontains $target) { continue }
    if ($hiddenProperties -contains $target) {
        Write-Ok "deferred CustomActionData hidden from the MSI log: $target"
    } else {
        Write-Fail "'$target' holds the CustomActionData of deferred action '$target' but is not in MsiHiddenProperties"
    }
}

# msidbServiceControlEventStart (0x1). Without it the MSI installs a service and never starts it,
# so the machine is left with a registered-but-stopped MiniVault after a successful installation.
foreach ($row in @($components['caphyon.advinst.msicomp.MsiServCtrlComponent'].ROW)) {
    $eventBits = [int]$row.Event
    if ($eventBits -band 0x1) {
        Write-Ok "ServiceControl '$($row.ServiceControl)' Event=$eventBits starts the service on install (0x1)"
    } else {
        Write-Fail "ServiceControl '$($row.ServiceControl)' Event=$eventBits does not set the start-on-install bit (0x1)"
    }
}

# ---------------------------------------------------------------------------
# 6. Configuration dialogs
# ---------------------------------------------------------------------------
Write-Section '6. Configuration dialogs'

# Dialogs the theme fragments provide (not declared in this file) that our rows may reference.
$fragmentDialogs = @('WelcomeDlg', 'LicenseAgreementDlg', 'FolderDlg', 'VerifyReadyDlg', 'ExitDialog', 'CancelDlg',
    'ProgressDlg', 'MaintenanceWelcomeDlg', 'MaintenanceTypeDlg', 'VerifyRemoveDlg', 'VerifyRepairDlg', 'CustomizeDlg',
    'PatchWelcomeDlg', 'ResumeDlg', 'FatalError', 'UserExit', 'BrowseDlg', 'DiskCostDlg')

$ownDialogs = @()
if ($components.ContainsKey('caphyon.advinst.msicomp.MsiDialogComponent')) {
    $ownDialogs = @($components['caphyon.advinst.msicomp.MsiDialogComponent'].ROW | ForEach-Object { $_.Dialog })
}
$controlRows = @()
if ($components.ContainsKey('caphyon.advinst.msicomp.MsiControlComponent')) {
    $controlRows = @($components['caphyon.advinst.msicomp.MsiControlComponent'].ROW)
}
$eventRows = @()
if ($components.ContainsKey('caphyon.advinst.msicomp.MsiControlEventComponent')) {
    $eventRows = @($components['caphyon.advinst.msicomp.MsiControlEventComponent'].ROW)
}
$conditionRows = @()
if ($components.ContainsKey('caphyon.advinst.msicomp.MsiControlConditionComponent')) {
    $conditionRows = @($components['caphyon.advinst.msicomp.MsiControlConditionComponent'].ROW)
}
$checkBoxRows = @()
if ($components.ContainsKey('caphyon.advinst.msicomp.MsiCheckBoxComponent')) {
    $checkBoxRows = @($components['caphyon.advinst.msicomp.MsiCheckBoxComponent'].ROW)
}

# Dialogs without the wizard chrome (none at the moment: messages are MessageBoxes from ShowUiMessage).
$messageDialogs = @()
foreach ($dialog in @('SqlDlg', 'ServiceDlg', 'TlsDlg', 'RecoveryDlg')) {
    if ($ownDialogs -contains $dialog) { Write-Ok "dialog declared: $dialog" } else { Write-Fail "dialog not declared: $dialog" }
}

function Test-ControlExists {
    param([string]$Dialog, [string]$Control)
    return @($controlRows | Where-Object { $_.Dialog_ -eq $Dialog -and $_.Control -eq $Control }).Count -eq 1
}

# Every page we own has the wizard chrome and its default/cancel buttons exist.
foreach ($row in @($components['caphyon.advinst.msicomp.MsiDialogComponent'].ROW)) {
    foreach ($required in @($row.Control_Default, $row.Control_Cancel)) {
        if (-not (Test-ControlExists $row.Dialog $required)) {
            Write-Fail "dialog '$($row.Dialog)' names control '$required' as default/cancel but has no such control"
        }
    }
    if ($messageDialogs -notcontains $row.Dialog) {
        foreach ($required in @('Back', 'Next', 'Cancel', 'Title', 'BannerBitmap', 'BannerLine', 'BottomLine')) {
            if (-not (Test-ControlExists $row.Dialog $required)) {
                Write-Fail "dialog '$($row.Dialog)' has no '$required' control"
            }
        }
    }
}
Write-Ok "every declared dialog has its chrome, default and cancel controls (unless reported above)"

# Controls: unique keys, bound properties declared, check boxes listed in the CheckBox table.
$controlKeys = @{}
foreach ($row in $controlRows) {
    $key = "$($row.Dialog_)#$($row.Control)"
    if ($controlKeys.ContainsKey($key)) { Write-Fail "duplicate control: $key" }
    $controlKeys[$key] = $true

    $bound = if ($row.PSObject.Properties.Name -contains 'Property') { $row.Property } else { '' }
    if ($bound) {
        if (-not $knownProperties.ContainsKey($bound)) {
            Write-Fail "control $key binds unknown property $bound (no Property row, not in SecureCustomProperties/MsiHiddenProperties, no CheckBox row, never set by an event)"
        }
        if ($row.Type -eq 'CheckBox' -and @($checkBoxRows | Where-Object { $_.Property -eq $bound }).Count -ne 1) {
            Write-Fail "check box $key has no CheckBox-table row for $bound (its ticked value would be undefined)"
        }
        if ($row.Type -eq 'ComboBox') {
            $comboRows = @()
            if ($components.ContainsKey('caphyon.advinst.msicomp.MsiComboBoxComponent')) {
                $comboRows = @($components['caphyon.advinst.msicomp.MsiComboBoxComponent'].ROW | Where-Object { $_.Property -eq $bound })
            }
            if ($comboRows.Count -lt 2) {
                Write-Fail "combo box $key has fewer than two ComboBox-table rows for $bound"
            } elseif ($properties.ContainsKey($bound) -and @($comboRows | Where-Object { $_.Value -eq $properties[$bound] }).Count -ne 1) {
                Write-Fail "combo box ${key}: the default value '$($properties[$bound])' of $bound is not one of its entries"
            }
            # A ComboBox cannot publish ControlEvents (MSI), so nothing may hang an event off it.
            if (@($eventRows | Where-Object { $_.Dialog_ -eq $row.Dialog_ -and $_.Control_ -eq $row.Control }).Count -gt 0) {
                Write-Fail "combo box $key has ControlEvent rows; MSI combo boxes cannot publish events"
            }
        }
        if ($row.Type -eq 'RadioButtonGroup') {
            $radioRows = @()
            if ($components.ContainsKey('caphyon.advinst.msicomp.MsiRadioButtonComponent')) {
                $radioRows = @($components['caphyon.advinst.msicomp.MsiRadioButtonComponent'].ROW | Where-Object { $_.Property -eq $bound })
            }
            if ($radioRows.Count -lt 2) {
                Write-Fail "radio button group $key has fewer than two RadioButton-table rows for $bound"
            } elseif ($properties.ContainsKey($bound) -and @($radioRows | Where-Object { $_.Value -eq $properties[$bound] }).Count -ne 1) {
                Write-Fail "radio button group ${key}: the default value '$($properties[$bound])' of $bound is not one of its buttons"
            }
        }
        if ($row.Type -eq 'Edit' -and ([int]$row.Attributes -band 0x200000) -and ($hiddenProperties -notcontains $bound)) {
            Write-Fail "password edit $key binds $bound, which is not in MsiHiddenProperties"
        }
    }
}
Write-Ok "$($controlRows.Count) control rows: keys unique, bound properties declared, password edits hidden (unless reported above)"

# Events: dialog/control pairs exist (for our dialogs), NewDialog targets exist, DoAction targets are custom actions.
$customActionNames = @($customActions | ForEach-Object { $_.Action })
foreach ($row in $eventRows) {
    $key = "$($row.Dialog_)#$($row.Control_)"
    if (($ownDialogs -contains $row.Dialog_) -and -not (Test-ControlExists $row.Dialog_ $row.Control_)) {
        Write-Fail "control event on missing control: $key"
    }
    if (($fragmentDialogs -notcontains $row.Dialog_) -and ($ownDialogs -notcontains $row.Dialog_)) {
        Write-Fail "control event on unknown dialog: $key"
    }
    switch ($row.Event) {
        'NewDialog'   { if (($ownDialogs -notcontains $row.Argument) -and ($fragmentDialogs -notcontains $row.Argument)) { Write-Fail "$key -> NewDialog '$($row.Argument)': no such dialog" } }
        'SpawnDialog' { if (($ownDialogs -notcontains $row.Argument) -and ($fragmentDialogs -notcontains $row.Argument)) { Write-Fail "$key -> SpawnDialog '$($row.Argument)': no such dialog" } }
        'DoAction'    { if ($customActionNames -notcontains $row.Argument) { Write-Fail "$key -> DoAction '$($row.Argument)': no such custom action" } }
        default {
            if ($row.Event -match '^\[(.+)\]$') {
                $target = $Matches[1]
                if ($target -ne 'AiRefreshDlg' -and -not $knownProperties.ContainsKey($target)) {
                    Write-Fail "$key sets unknown property $target"
                }
            }
        }
    }
}
Write-Ok "$($eventRows.Count) control events resolve to known dialogs, controls, actions and properties (unless reported above)"

# Each of our pages must be reachable and must lead on: FolderDlg -> SqlDlg -> ... -> VerifyReadyDlg, and back.
# The theme fragments provide the standard dialogs but not their page-to-page events: a project that
# lacks them builds fine and then WelcomeDlg's Next button does nothing. So the whole first-install
# chain is required here, plus the maintenance/patch/resume ways out.
$navigation = @(
    @('WelcomeDlg', 'FolderDlg'), @('FolderDlg', 'WelcomeDlg'),
    @('FolderDlg', 'SqlDlg'), @('SqlDlg', 'ServiceDlg'), @('ServiceDlg', 'TlsDlg'), @('TlsDlg', 'RecoveryDlg'), @('RecoveryDlg', 'VerifyReadyDlg'),
    @('VerifyReadyDlg', 'RecoveryDlg'), @('RecoveryDlg', 'TlsDlg'), @('TlsDlg', 'ServiceDlg'), @('ServiceDlg', 'SqlDlg'), @('SqlDlg', 'FolderDlg'),
    @('FolderDlg', 'VerifyReadyDlg'), @('VerifyReadyDlg', 'FolderDlg'),
    @('MaintenanceWelcomeDlg', 'MaintenanceTypeDlg'), @('MaintenanceTypeDlg', 'CustomizeDlg'), @('MaintenanceTypeDlg', 'VerifyRepairDlg'),
    @('MaintenanceTypeDlg', 'VerifyRemoveDlg'), @('CustomizeDlg', 'VerifyReadyDlg'), @('PatchWelcomeDlg', 'VerifyReadyDlg')
)
foreach ($hop in $navigation) {
    if (@($eventRows | Where-Object { $_.Dialog_ -eq $hop[0] -and $_.Event -eq 'NewDialog' -and $_.Argument -eq $hop[1] }).Count -ge 1) {
        Write-Ok "navigation: $($hop[0]) -> $($hop[1])"
    } else {
        Write-Fail "navigation missing: $($hop[0]) -> $($hop[1])"
    }
}
foreach ($commit in @(@('VerifyReadyDlg', 'Install'), @('VerifyRepairDlg', 'Repair'), @('VerifyRemoveDlg', 'Remove'), @('ResumeDlg', 'Install'))) {
    if (@($eventRows | Where-Object { $_.Dialog_ -eq $commit[0] -and $_.Control_ -eq $commit[1] -and $_.Event -eq 'EndDialog' -and $_.Argument -eq 'Return' }).Count -ge 1) {
        Write-Ok "commit: $($commit[0]).$($commit[1]) -> EndDialog Return"
    } else {
        Write-Fail "commit missing: $($commit[0]).$($commit[1]) has no EndDialog Return (the wizard could never start the install)"
    }
}

# Every Next button of ours that can refuse must have a NewDialog with a condition (never unconditional) so the
# validation events actually gate it.
foreach ($dialog in @('SqlDlg', 'ServiceDlg', 'TlsDlg', 'RecoveryDlg')) {
    $next = @($eventRows | Where-Object { $_.Dialog_ -eq $dialog -and $_.Control_ -eq 'Next' -and $_.Event -eq 'NewDialog' })
    $forward = @($next | Where-Object { $messageDialogs -notcontains $_.Argument })
    if ($forward.Count -ne 1 -or $forward[0].Condition -eq '1' -or [string]::IsNullOrWhiteSpace($forward[0].Condition)) {
        Write-Fail "$dialog.Next must have exactly one conditional NewDialog to the next page (message pages aside)"
    }
    foreach ($row in $next) {
        if ($row.Condition -eq '1' -or [string]::IsNullOrWhiteSpace($row.Condition)) { Write-Fail "$dialog.Next has an unconditional NewDialog to $($row.Argument)" }
    }
}

# A managed action run from a dialog needs its Type-51 data setter run right before it (that is how
# DotNetMethodCaller learns which assembly/method to call). Check every DoAction to a managed action.
$setterFor = @{}
foreach ($setter in @($customActions | Where-Object { [int]$_.Type -eq 51 -and $_.Source -eq 'CustomActionData' })) {
    foreach ($action in @($customActions | Where-Object { $_.PSObject.Properties.Name -contains 'AdditionalSeq' -and $_.AdditionalSeq -eq $setter.Action })) {
        $setterFor[$action.Action] = $setter.Action
    }
}
$dialogDoActions = @($eventRows | Where-Object { $_.Event -eq 'DoAction' -and $setterFor.ContainsKey($_.Argument) })
foreach ($doAction in $dialogDoActions) {
    $siblings = @($eventRows | Where-Object { $_.Dialog_ -eq $doAction.Dialog_ -and $_.Control_ -eq $doAction.Control_ } | Sort-Object { [int]$_.Ordering })
    $index = [array]::IndexOf(@($siblings | ForEach-Object { "$($_.Event)|$($_.Argument)|$($_.Ordering)" }), "DoAction|$($doAction.Argument)|$($doAction.Ordering)")
    $previous = if ($index -gt 0) { $siblings[$index - 1] } else { $null }
    if ($null -ne $previous -and $previous.Event -eq 'DoAction' -and $previous.Argument -eq $setterFor[$doAction.Argument]) {
        Write-Ok "$($doAction.Dialog_).$($doAction.Control_): $($setterFor[$doAction.Argument]) > $($doAction.Argument)"
    } else {
        Write-Fail "$($doAction.Dialog_).$($doAction.Control_) runs $($doAction.Argument) without DoAction $($setterFor[$doAction.Argument]) immediately before it"
    }
}
# The SQL page must actually test something: compose, test, show the result.
$testSequence = @($eventRows | Where-Object { $_.Dialog_ -eq 'SqlDlg' -and $_.Control_ -eq 'TestButton' -and $_.Event -eq 'DoAction' } | Sort-Object { [int]$_.Ordering } | ForEach-Object { $_.Argument }) -join ' > '
$expectedTest = 'AI_DATA_SETTER_5 > BuildConnectionString > AI_DATA_SETTER_2 > TestSqlConnection > AI_DATA_SETTER_7 > ShowUiMessage'
if ($testSequence -eq $expectedTest) {
    Write-Ok "SqlDlg.TestButton: $testSequence"
} else {
    Write-Fail "SqlDlg.TestButton actions are '$testSequence'; expected '$expectedTest'"
}
# Results and validation messages are MessageBoxes opened by the ShowUiMessage custom action (the
# InfraskopeServer setup's pattern). SpawnDialog from a control that also refreshes the page is
# silently dropped by Advanced Installer's UI engine, and NewDialog replaces the wizard window, so
# neither may be used for a message; every [MV_UI_ERROR] setter must be followed by ShowUiMessage on
# the same control.
if (@($eventRows | Where-Object { $_.Dialog_ -eq 'SqlDlg' -and $_.Control_ -eq 'TestButton' -and $_.Event -eq 'DoAction' -and $_.Argument -eq 'ShowUiMessage' }).Count -eq 1) {
    Write-Ok 'SqlDlg.TestButton shows MV_SQL_RESULT through ShowUiMessage'
} else {
    Write-Fail 'SqlDlg.TestButton does not run ShowUiMessage with the test result'
}
foreach ($spawn in @($eventRows | Where-Object { $_.Event -eq 'SpawnDialog' -and $_.Argument -ne 'CancelDlg' -and ($ownDialogs -contains $_.Dialog_) })) {
    Write-Fail "$($spawn.Dialog_).$($spawn.Control_) spawns '$($spawn.Argument)': SpawnDialog is unreliable here, use ShowUiMessage"
}
foreach ($stray in @($eventRows | Where-Object { $_.Event -eq 'NewDialog' -and $_.Argument -match 'MsgDlg$|MvErrorDlg' })) {
    Write-Fail "$($stray.Dialog_).$($stray.Control_) opens message page '$($stray.Argument)': use ShowUiMessage instead"
}
$messageSetters = @($eventRows | Where-Object { $_.Event -eq '[MV_UI_ERROR]' -and $_.Argument -ne '{}' } | ForEach-Object { "$($_.Dialog_)#$($_.Control_)" } | Sort-Object -Unique)
foreach ($key in $messageSetters) {
    $parts = $key -split '#'
    if (@($eventRows | Where-Object { $_.Dialog_ -eq $parts[0] -and $_.Control_ -eq $parts[1] -and $_.Event -eq 'DoAction' -and $_.Argument -eq 'ShowUiMessage' }).Count -ge 1) {
        Write-Ok "$key sets MV_UI_ERROR and shows it"
    } else {
        Write-Fail "$key sets MV_UI_ERROR but never runs ShowUiMessage"
    }
}

# Control conditions reference existing controls.
foreach ($row in $conditionRows) {
    if (($ownDialogs -contains $row.Dialog_) -and -not (Test-ControlExists $row.Dialog_ $row.Control_)) {
        Write-Fail "control condition on missing control: $($row.Dialog_)#$($row.Control_)"
    }
}
Write-Ok "$($conditionRows.Count) control conditions reference existing controls (unless reported above)"

# ---------------------------------------------------------------------------
# 7. Build with Advanced Installer (-Build)
# ---------------------------------------------------------------------------
if ($Build) {
    Write-Section '7. Build with AdvancedInstaller.com'

    if ([string]::IsNullOrWhiteSpace($AdvancedInstallerCom)) {
        $caphyon = Join-Path ${env:ProgramFiles(x86)} 'Caphyon'
        $candidates = @()
        if (Test-Path -LiteralPath $caphyon) {
            $candidates = @(Get-ChildItem -LiteralPath $caphyon -Directory -Filter 'Advanced Installer*' |
                Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'bin\x86\AdvancedInstaller.com' } |
                Where-Object { Test-Path -LiteralPath $_ })
        }
        if ($candidates.Count -gt 0) { $AdvancedInstallerCom = $candidates[0] }
    }

    if ([string]::IsNullOrWhiteSpace($AdvancedInstallerCom) -or -not (Test-Path -LiteralPath $AdvancedInstallerCom)) {
        Write-Fail 'AdvancedInstaller.com not found (install Advanced Installer, or pass -AdvancedInstallerCom)'
    } else {
        Write-Ok "using $AdvancedInstallerCom"

        # Build a copy: /build may convert and normalize the project; the tracked file stays untouched.
        $resolvedAip = (Resolve-Path -LiteralPath $AipPath).Path
        $checkAip = [System.IO.Path]::Combine(
            [System.IO.Path]::GetDirectoryName($resolvedAip),
            [System.IO.Path]::GetFileNameWithoutExtension($resolvedAip) + '.check.aip')
        Copy-Item -LiteralPath $resolvedAip -Destination $checkAip -Force

        $output = & $AdvancedInstallerCom /build $checkAip 2>&1 | ForEach-Object { "$_" }
        $buildExit = $LASTEXITCODE

        $iceLines = @($output | Where-Object { $_ -match 'AI_ICE\d+' })
        $errorLines = @($output | Where-Object { $_ -match '^ERROR:|^Error:' -and $_ -notmatch 'AI_ICE\d+' })
        $otherLines = @($output | Where-Object { $_ -notmatch '^Notification: File added' -and $_ -notmatch 'AI_ICE\d+' -and $_.Trim() })

        foreach ($line in $otherLines) { Write-Host "         $line" -ForegroundColor DarkGray }
        if ($iceLines.Count -gt 0) {
            Write-Host "  [info] $($iceLines.Count) AI_ICE validation line(s) (do not fail the build):" -ForegroundColor Yellow
            foreach ($line in $iceLines) { Write-Host "         $line" -ForegroundColor Yellow }
        }

        if ($buildExit -eq 0 -and $errorLines.Count -eq 0) {
            $msiName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedAip) + '.msi'
            $built = @(Get-ChildItem -LiteralPath (Split-Path -Parent $resolvedAip) -Recurse -Filter $msiName -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1)
            if ($built.Count -eq 1) {
                Write-Ok "AdvancedInstaller.com /build succeeded: $($built[0].FullName)"
            } else {
                Write-Ok 'AdvancedInstaller.com /build succeeded'
            }
        } else {
            Write-Fail "AdvancedInstaller.com /build failed (exit $buildExit): $(@($errorLines + ($output | Select-Object -Last 1)) -join ' | ')"
        }
    }
}

# ---------------------------------------------------------------------------
Write-Host ''
if ($script:failures.Count -eq 0) {
    Write-Host 'verify-aip: OK' -ForegroundColor Green
    Write-Host 'Note: this only validates the project file. Building the MSI requires Advanced Installer.'
    exit 0
}

Write-Host "verify-aip: $($script:failures.Count) problem(s)" -ForegroundColor Red
foreach ($failure in $script:failures) { Write-Host "  - $failure" -ForegroundColor Red }
exit 1
