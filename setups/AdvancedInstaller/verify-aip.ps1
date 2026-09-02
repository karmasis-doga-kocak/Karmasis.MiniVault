#Requires -Version 5.1
<#
.SYNOPSIS
    Static checks on Karmasis.MiniVault.aip. Does NOT build an MSI (that needs Advanced Installer).

.DESCRIPTION
    1. The .aip parses as XML and has the components the setup relies on.
    2. Lists every custom action row with its binary, type and sequencing, and checks that each
       managed custom action names a method that exists in the custom-actions assembly's source.
    3. Every MsiFilesComponent SourcePath and the SynchronizedFolderComponent SourcePath resolve to
       something that exists (run 'dotnet publish src/MiniVault.Server -p:PublishProfile=win-x64'
       first, or pass -SkipPayload).
    4. The custom-actions assembly paths referenced from the .aip match the project's output path,
       and (unless -SkipPayload) the built DLL is there.
    5. Every secret is hidden from the MSI log - the MV_* properties an operator types AND the
       CustomActionData property of every deferred managed custom action, which carries copies of
       them - the properties the custom actions read exist, and the service is actually started on
       install.

    Exits 0 when everything checks out, 1 otherwise.

.EXAMPLE
    .\setups\AdvancedInstaller\verify-aip.ps1

.EXAMPLE
    .\setups\AdvancedInstaller\verify-aip.ps1 -SkipPayload
#>
[CmdletBinding()]
param(
    [string]$AipPath,

    # Skip the checks that need 'dotnet publish' / a built custom-actions assembly.
    [switch]$SkipPayload
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
    'caphyon.advinst.msicomp.MsiLockPermissionsComponent',
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
        Write-Fail "MsiFilesComponent '$($row.File)' points at a missing file: $full (run: dotnet publish src/MiniVault.Server -p:PublishProfile=win-x64)"
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
        Write-Fail "synchronized folder is missing: $full (run: dotnet publish src/MiniVault.Server -p:PublishProfile=win-x64)"
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
    $namespaceManager = New-Object System.Xml.XmlNamespaceManager($projectXml.NameTable)
    $namespaceManager.AddNamespace('msb', 'http://schemas.microsoft.com/developer/msbuild/2003')

    $outputPaths = @($projectXml.SelectNodes('//msb:OutputPath', $namespaceManager) |
        ForEach-Object { $_.InnerText } | Select-Object -Unique)
    if ($outputPaths.Count -eq 0) {
        # SDK-style project: bin\<Configuration>\<TargetFramework>\.
        $outputPaths = @('bin\Release\')
    }
    $assemblyNameNode = $projectXml.SelectSingleNode('//msb:AssemblyName', $namespaceManager)
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
            Write-Fail "built assembly missing: $referenced (build Karmasis.MiniVault.Setup.sln first)"
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
    if (-not $properties.ContainsKey($secret)) {
        Write-Fail "property not declared: $secret"
    } elseif ($hiddenProperties -contains $secret) {
        Write-Ok "hidden from the MSI log: $secret"
    } else {
        Write-Fail "$secret is not in MsiHiddenProperties, so msiexec /l*v would write it to the log"
    }
}

# Properties the custom actions read but that carry no secret.
foreach ($property in @('MV_RECONFIGURE', 'MV_SERVICEACCOUNT')) {
    if ($properties.ContainsKey($property)) {
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
Write-Host ''
if ($script:failures.Count -eq 0) {
    Write-Host 'verify-aip: OK' -ForegroundColor Green
    Write-Host 'Note: this only validates the project file. Building the MSI requires Advanced Installer.'
    exit 0
}

Write-Host "verify-aip: $($script:failures.Count) problem(s)" -ForegroundColor Red
foreach ($failure in $script:failures) { Write-Host "  - $failure" -ForegroundColor Red }
exit 1
