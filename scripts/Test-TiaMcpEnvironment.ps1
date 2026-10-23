#Requires -Version 5.1
<#
.SYNOPSIS
Read-only deployment diagnostics. Emits one bounded JSON report; never starts TIA or PLCSIM.
.DESCRIPTION
Inspects known installation metadata through Probe-Environment.ps1 and the selected
x64 .NET directory. -EvidencePath accepts schema-v1 captured/synthetic evidence and
performs no machine probes. -OutputPath optionally saves the same report. Results
describe prerequisites, never license, API attachment or simulator qualification.
#>
[CmdletBinding()]
param(
    [string]$BundlePath,
    [string]$DotnetRoot,
    [string]$TiaApiDirectory,
    [string]$AdvancedSimulatorDirectory,
    [string]$AdvancedRuntimeDirectory,
    [string]$EvidencePath,
    [string]$OutputPath,
    [switch]$RequireSimulation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$packageFiles = @('host/TiaMcp.Host.exe','host/TiaMcp.Host.dll','host/TiaMcp.Host.deps.json',
    'host/TiaMcp.Host.runtimeconfig.json','workers/openness/TiaMcp.Openness.V21.exe','workers/simulator/TiaMcp.Simulator.V8.exe')

function Read-Field {
    param($Object, [string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Read-FailureStatus {
    param($Record)
    if ($Record.CategoryInfo.Category -eq 'ObjectNotFound' -or
        $Record.Exception -is [IO.FileNotFoundException] -or
        $Record.Exception -is [IO.DirectoryNotFoundException]) { return 'missing' }
    return 'unavailable'
}

function Read-FileMetadata {
    param([string]$Path, [switch]$Pe)
    $stream = $null
    $reader = $null
    try {
        $item = Get-Item -LiteralPath $Path -ErrorAction Stop
        if ($item.PSIsContainer) { return [pscustomobject]@{status='mismatch'; path=$Path; reason='Expected a file.'} }
        $result = [ordered]@{status='present'; path=$item.FullName}
        if ($Pe) {
            $stream = [IO.File]::Open($item.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $reader = New-Object IO.BinaryReader($stream)
            if ($reader.ReadUInt16() -ne 0x5a4d -or $stream.Length -lt 64) { throw 'Not a PE image.' }
            $stream.Position = 0x3c
            $offset = $reader.ReadInt32()
            if ($offset -lt 64 -or $offset -gt ($stream.Length - 6)) { throw 'Invalid PE offset.' }
            $stream.Position = $offset
            if ($reader.ReadUInt32() -ne 0x4550) { throw 'Invalid PE signature.' }
            $machine = $reader.ReadUInt16()
            $result.architecture = switch ($machine) { 0x8664 {'x64'} 0x14c {'x86'} 0xaa64 {'arm64'} default {'unknown'} }
        }
        return [pscustomobject]$result
    }
    catch { return [pscustomobject]@{status=(Read-FailureStatus $_); path=$Path; reason='File or PE metadata could not be read.'} }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        elseif ($null -ne $stream) { $stream.Dispose() }
    }
}

function Read-JsonFile {
    param([string]$Path)
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.PSIsContainer -or $item.Length -gt 1048576) { throw 'Expected a JSON file of at most 1 MiB.' }
    return ([IO.File]::ReadAllText($item.FullName) | ConvertFrom-Json)
}

function Get-HostEvidence {
    $root = $DotnetRoot
    if (-not $root) {
        $programFiles64 = $env:ProgramW6432
        if (-not $programFiles64) { $programFiles64 = $env:ProgramFiles }
        $root = Join-Path $programFiles64 'dotnet'
    }
    $families = @()
    foreach ($family in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
        try {
            $directories = @(Get-ChildItem -LiteralPath (Join-Path $root ('shared/' + $family)) -Directory -ErrorAction Stop |
                Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending)
            $versions = @($directories | Select-Object -First 32 |
                ForEach-Object {
                    $marker = if ($family -eq 'Microsoft.NETCore.App') { 'coreclr.dll' } else { 'Microsoft.AspNetCore.App.deps.json' }
                    [pscustomobject]@{version=$_.Name; marker=(Read-FileMetadata (Join-Path $_.FullName $marker))}
                })
            $families += [pscustomobject]@{name=$family; status='read'; versions=$versions;truncated=($directories.Count -gt 32)}
        }
        catch { $families += [pscustomobject]@{name=$family; status=(Read-FailureStatus $_); versions=@()} }
    }
    $package = [pscustomobject]@{status='not_requested'; files=@()}
    if ($BundlePath) {
        $files = @()
        foreach ($relative in $packageFiles) {
            $files += [pscustomobject]@{relativePath=$relative; metadata=(Read-FileMetadata (Join-Path $BundlePath $relative) -Pe:($relative.EndsWith('.exe')))}
        }
        $package = [pscustomobject]@{status='read'; files=$files; runtimeConfig=$null; runtimeConfigStatus='unavailable'}
        try {
            $package.runtimeConfig = Read-JsonFile (Join-Path $BundlePath 'host/TiaMcp.Host.runtimeconfig.json')
            $package.runtimeConfigStatus = 'read'
        }
        catch { $package.runtimeConfigStatus = Read-FailureStatus $_ }
    }
    return [pscustomobject]@{
        is64BitOperatingSystem=[Environment]::Is64BitOperatingSystem
        osVersion=[Environment]::OSVersion.VersionString
        osArchitecture=$(if ($env:PROCESSOR_ARCHITEW6432) {$env:PROCESSOR_ARCHITEW6432} else {$env:PROCESSOR_ARCHITECTURE})
        processArchitecture= $(if ([Environment]::Is64BitProcess) {'64-bit'} else {'32-bit'})
        dotnetExecutable=(Read-FileMetadata (Join-Path $root 'dotnet.exe') -Pe)
        runtimeFamilies=$families
        package=$package
    }
}

$mode = 'local-metadata'
if ($EvidencePath) {
    if ($BundlePath -or $DotnetRoot -or $TiaApiDirectory -or $AdvancedSimulatorDirectory -or $AdvancedRuntimeDirectory) {
        throw 'EvidencePath cannot be combined with installation path overrides.'
    }
    $evidence = Read-JsonFile $EvidencePath
    if ((Read-Field $evidence 'schemaVersion') -ne 1 -or $null -eq (Read-Field $evidence 'probe') -or $null -eq (Read-Field $evidence 'host')) {
        throw 'Evidence must contain schemaVersion 1, probe and host objects.'
    }
    $mode = 'supplied-evidence'
}
else {
    $probeArgs = @{}
    foreach ($name in @('TiaApiDirectory','AdvancedSimulatorDirectory','AdvancedRuntimeDirectory')) {
        $value = Get-Variable -Name $name -ValueOnly
        if ($value) { $probeArgs[$name] = $value }
    }
    try { $probe = (& (Join-Path $PSScriptRoot 'Probe-Environment.ps1') @probeArgs) | ConvertFrom-Json }
    catch { $probe = [pscustomobject]@{probeFailure='unavailable'} }
    $evidence = [pscustomobject]@{schemaVersion=1; probe=$probe; host=(Get-HostEvidence)}
}
$probe = $evidence.probe
$hostEvidence = $evidence.host
$checks = New-Object 'System.Collections.Generic.List[object]'
function Add-Check {
    param([string]$Id, [string]$Capability, [bool]$Required, [string]$Status, [string]$Summary, [string]$Action, $Observed=$null)
    if ($Status -notin @('present','missing','unavailable','mismatch','unverified')) { $Status = 'unavailable' }
    $checks.Add([pscustomobject]@{id=$Id; capability=$Capability; required=$Required; status=$Status; summary=$Summary; action=$Action; observed=$Observed})
}
function Evidence-Status {
    param($Object)
    $state = Read-Field $Object 'status' 'unavailable'
    if ($state -in @('read','present')) { return 'present' }
    if ($state -eq 'missing') { return 'missing' }
    return 'unavailable'
}

$os64 = Read-Field $hostEvidence 'is64BitOperatingSystem'
$osStatus = if ($os64 -eq $true) {'present'} elseif ($os64 -eq $false) {'mismatch'} else {'unavailable'}
Add-Check 'windows.x64' 'host' $true $osStatus 'The native workers require a 64-bit Windows environment.' 'Use the Windows x64 deployment target; this check does not qualify the Windows version against Siemens support.' $os64
$dotnet = Read-Field $hostEvidence 'dotnetExecutable'
$dotnetStatus = Evidence-Status $dotnet
if ($dotnetStatus -eq 'present' -and (Read-Field $dotnet 'architecture') -ne 'x64') { $dotnetStatus = 'mismatch' }
Add-Check 'dotnet.x64' 'host' $true $dotnetStatus 'Selected .NET installation must contain the x64 host.' 'Install the x64 .NET 10 Runtime and ASP.NET Core Runtime 10, or select the correct installation with -DotnetRoot. Do not substitute the x86 runtime.' (Read-Field $dotnet 'architecture')

$requirements = @([pscustomobject]@{name='Microsoft.NETCore.App';version='10.0.0'},[pscustomobject]@{name='Microsoft.AspNetCore.App';version='10.0.0'})
$package = Read-Field $hostEvidence 'package'
if ((Read-Field $package 'status') -eq 'read') {
    foreach ($relative in $packageFiles) {
        $found = @(Read-Field $package 'files' @() | Where-Object { (Read-Field $_ 'relativePath') -eq $relative } | Select-Object -First 1)
        $capability = if ($relative -like 'workers/simulator/*') {'simulation'} elseif ($relative -like 'workers/openness/*') {'offline'} else {'host'}
        $metadata = if ($found.Count -eq 1) { Read-Field $found[0] 'metadata' } else { $null }
        $status = Evidence-Status $metadata
        if ($status -eq 'present' -and $relative.EndsWith('.exe') -and (Read-Field $metadata 'architecture') -ne 'x64') { $status = 'mismatch' }
        Add-Check ('package.' + $relative) $capability $true $status 'Bundled executable or host metadata file.' 'Extract the complete verified bundle again; use the x64 Windows package.' (Read-Field $metadata 'architecture')
    }
    $config = Read-Field (Read-Field $package 'runtimeConfig') 'runtimeOptions'
    $frameworks = @(Read-Field $config 'frameworks' @())
    $validConfig = (Read-Field $package 'runtimeConfigStatus') -eq 'read' -and $frameworks.Count -eq 2
    foreach ($expected in $requirements) {
        $match = @($frameworks | Where-Object { (Read-Field $_ 'name') -eq $expected.name -and (Read-Field $_ 'version') -match '^10\.\d+\.\d+$' })
        if ($match.Count -ne 1) { $validConfig = $false }
    }
    if ($null -ne (Read-Field $config 'rollForward')) { $validConfig = $false }
    if ($validConfig) { $requirements = $frameworks }
    Add-Check 'package.runtimeconfig' 'host' $true $(if ($validConfig) {'present'} else {'unavailable'}) 'Runtime prerequisites are read from the selected bundle when its configuration matches this deployment profile.' 'Use this release runtimeconfig unchanged. Custom frameworks or roll-forward policies require separate validation.'
}
else { Add-Check 'package.files' 'host' $false 'unverified' 'No bundle was selected for file inspection.' 'Run again with -BundlePath pointing to the extracted release.' }

foreach ($requirement in $requirements) {
    $family = @(Read-Field $hostEvidence 'runtimeFamilies' @() | Where-Object { (Read-Field $_ 'name') -eq $requirement.name } | Select-Object -First 1)
    $status = 'unavailable'
    $versions = @()
    if ($family.Count -eq 1) {
        $status = Evidence-Status $family[0]
        if ($status -eq 'present') {
            $minimum = [version]$requirement.version
            $compatible = @(Read-Field $family[0] 'versions' @() | Where-Object {
                $version = Read-Field $_ 'version' ''
                if ($version -notmatch '^\d+\.\d+\.\d+$') { return $false }
                $parsed = [version]$version
                return $parsed.Major -eq $minimum.Major -and $parsed -ge $minimum
            })
            $versions = @($compatible | ForEach-Object { Read-Field $_ 'version' } | Select-Object -First 8)
            if ($compatible.Count -eq 0) { $status = if ((Read-Field $family[0] 'truncated') -eq $true) {'unavailable'} else {'missing'} }
            elseif (@($compatible | Where-Object { (Evidence-Status (Read-Field $_ 'marker')) -eq 'present' }).Count -gt 0) { $status = 'present' }
            elseif (@($compatible | Where-Object { (Evidence-Status (Read-Field $_ 'marker')) -eq 'unavailable' }).Count -gt 0) { $status = 'unavailable' }
            else { $status = 'missing' }
        }
    }
    Add-Check ('dotnet.' + $requirement.name) 'host' $true $status ('Required shared framework: ' + $requirement.name + ' ' + $requirement.version + ', compatible stable patch/minor on the same major.') 'Install or repair the x64 .NET 10 Runtime and ASP.NET Core Runtime 10. Check their shared framework files; SDK presence or a newer major alone does not establish this prerequisite.' $versions
}
$frameworkRuntime = Read-Field (Read-Field $probe 'framework') 'runtime'
$frameworkStatus = Evidence-Status $frameworkRuntime
if ($frameworkStatus -eq 'present' -and (Read-Field $frameworkRuntime 'supportsNet48') -ne $true) { $frameworkStatus = 'mismatch' }
Add-Check 'framework.net48' 'offline' $true $frameworkStatus 'Engineering and simulation workers target .NET Framework 4.8.' 'Install or enable .NET Framework 4.8 or later; the developer reference pack alone is insufficient.' (Read-Field $frameworkRuntime 'release')

$tia = Read-Field $probe 'tiaV21'
foreach ($name in @('Siemens.Engineering.Base','Siemens.Engineering.Step7','Siemens.Engineering.Safety')) {
    $matches = @(Read-Field $tia 'assemblies' @() | ForEach-Object { Read-Field $_ 'binary' } | Where-Object {
        (Read-Field (Read-Field $_ 'assembly') 'name') -eq $name -or [IO.Path]::GetFileNameWithoutExtension([string](Read-Field $_ 'path' '')) -eq $name
    } | Select-Object -First 1)
    $status = if ((Read-Field $tia 'enumerationStatus') -in @('read','missing')) {'missing'} else {'unavailable'}
    $version = $null
    if ($matches.Count -eq 1) {
        $status = Evidence-Status $matches[0]
        if ($status -eq 'present') {
            $assembly = Read-Field $matches[0] 'assembly'
            $status = Evidence-Status $assembly
            $version = Read-Field $assembly 'version'
            if ($status -eq 'present' -and ($version -notmatch '^21\.0\.0\.0$' -or (Read-Field $assembly 'name') -ne $name)) { $status = 'mismatch' }
        }
    }
    Add-Check ('tia.' + $name) 'offline' $true $status 'Installed Openness assembly identity must match the V21 worker.' 'Install/repair TIA V21 Openness or use -TiaApiDirectory for its PublicAPI\V21\net48 folder. Do not copy DLLs from another TIA version into the bundle.' $version
}

foreach ($groupSpec in @(@{name='Siemens TIA Openness';id='token.openness';capability='offline'},@{name='PlcSimUsers';id='token.simulation';capability='simulation'})) {
    $groups = @(Read-Field (Read-Field $probe 'currentToken') 'groups' @() | Where-Object { (Read-Field $_ 'name') -eq $groupSpec.name } | Select-Object -First 1)
    $status = 'unavailable'
    $observed = $null
    if ($groups.Count -eq 1) {
        $group = $groups[0]
        $observed = [pscustomobject]@{presentInCurrentToken=(Read-Field $group 'presentInCurrentToken');authorizedInCurrentToken=(Read-Field $group 'authorizedInCurrentToken')}
        if ((Read-Field $group 'status') -eq 'read' -and (Read-Field $group 'authorizedInCurrentToken') -eq $true) { $status = 'present' }
    }
    Add-Check $groupSpec.id $groupSpec.capability $true $status ('Effective authorization for ' + $groupSpec.name + ' in this process token.') 'Run this diagnostic in the same normal Windows user/session as the workers. Restricted-token or failed lookup results do not establish missing group membership. Confirm authorization there before changing groups; sign out/in after an administrator-approved membership change.' $observed
}

$advanced = Read-Field (Read-Field $probe 'simulators') 'advanced'
$apiMatches = @(Read-Field $advanced 'apiVersions' @() | Where-Object { (Read-Field $_ 'versionDirectory') -eq '8.0' } | Select-Object -First 1)
$apiStatus = if ((Read-Field (Read-Field $advanced 'apiEnumeration') 'status') -in @('read','missing')) {'missing'} else {'unavailable'}
$apiVersion = $null
if ($apiMatches.Count -eq 1) {
    $api = Read-Field $apiMatches[0] 'x64'
    $apiStatus = Evidence-Status $api
    if ($apiStatus -eq 'present') {
        $assembly = Read-Field $api 'assembly'
        $apiStatus = Evidence-Status $assembly
        $apiVersion = Read-Field $assembly 'version'
        if ($apiStatus -eq 'present' -and ($apiVersion -notmatch '^8\.0\.' -or (Read-Field $assembly 'name') -ne 'Siemens.Simatic.Simulation.Runtime.Api.x64')) { $apiStatus = 'mismatch' }
    }
}
Add-Check 'simulation.api.v8.x64' 'simulation' $true $apiStatus 'Simulation tools use the installed PLCSIM Advanced V8 x64 API.' 'Install/repair PLCSIM Advanced V8 on the worker machine, or select its Common Files runtime root. Standard PLCSIM installation is not evidence of this API prerequisite.' $apiVersion
Add-Check 'simulation.manager' 'simulation' $true (Evidence-Status (Read-Field $advanced 'runtimeManager')) 'PLCSIM Advanced Runtime Manager installation metadata.' 'Repair the Advanced runtime installation if this file is absent; this diagnostic does not launch it.'
Add-Check 'tia.attachment' 'offline' $false 'unverified' 'TIA attachment, interactive authorization and licensed features were not exercised.' 'Run the deployment qualification suite in the destination user session against an owned scratch project.'
Add-Check 'simulation.execution' 'simulation' $false 'unverified' 'License, native loading, CPU startup, RUN/read/write and timing remain unverified.' 'Run isolated simulator qualification after metadata checks pass. Installed runtime files do not prove licensing or successful PowerOn.'
Add-Check 'platform.vendorSupport' 'host' $false 'unverified' 'Siemens support for the exact Windows build, VM product and product combination is not inferred.' 'Compare the exact guest OS and Siemens product versions with the applicable Siemens installation requirements.'

function Summarize-Prerequisites {
    param([string[]]$Capabilities)
    $required = @($checks | Where-Object { $_.required -and $_.capability -in $Capabilities })
    $failed = @($required | Where-Object { $_.status -in @('missing','mismatch') } | ForEach-Object { $_.id })
    $unknown = @($required | Where-Object { $_.status -eq 'unavailable' } | ForEach-Object { $_.id })
    $state = if ($failed.Count -gt 0) {'blocked'} elseif ($unknown.Count -gt 0) {'unknown'} else {'present'}
    return [pscustomobject]@{status=$state; blockingCheckIds=$failed; unavailableCheckIds=$unknown; qualification='unverified'}
}
$report = [ordered]@{
    schemaVersion=1
    capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    mode=$mode
    environment=[ordered]@{
        osVersion=(Read-Field $hostEvidence 'osVersion')
        osArchitecture=(Read-Field $hostEvidence 'osArchitecture')
        dotnetExecutablePath=(Read-Field $dotnet 'path')
        tiaApiDirectory=(Read-Field (Read-Field $tia 'apiDirectory') 'path')
        advancedRuntimeManagerPath=(Read-Field (Read-Field $advanced 'runtimeManager') 'path')
        bundlePath=$(if ($mode -eq 'local-metadata') {$BundlePath} else {$null})
    }
    scope='Installation metadata and current token only; known paths or explicit overrides. No Siemens API initialization or licensing query.'
    simulationRequired=[bool]$RequireSimulation
    selectedPrerequisites=(Summarize-Prerequisites $(if ($RequireSimulation) {@('host','offline','simulation')} else {@('host','offline')}))
    capabilities=[ordered]@{host=(Summarize-Prerequisites @('host'));offline=(Summarize-Prerequisites @('host','offline'));simulation=(Summarize-Prerequisites @('host','offline','simulation'))}
    checks=@($checks.ToArray())
    settingsModified=$false
    nativeWorkersStarted=$false
    licensesInspected=$false
    limits=[ordered]@{maxEvidenceBytes=1048576;maxRuntimeVersionsPerFamily=32;maxReportedMatchingVersions=8;maxOutputBytes=65536}
}
$json = $report | ConvertTo-Json -Depth 12
if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 65536) { throw 'Diagnostic output exceeded the 64 KiB contract.' }
if ($OutputPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json, (New-Object Text.UTF8Encoding($false))) }
Write-Output $json
