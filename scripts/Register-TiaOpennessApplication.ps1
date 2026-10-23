#Requires -Version 5.1
<#
.SYNOPSIS
Register one verified TIA MCP V21 worker in Siemens' application AllowList.
.DESCRIPTION
Apply mode requires elevated 64-bit Windows and writes only the worker's official
Entry key. InspectOnly reads the prior entry and emits the exact proposal without
writing the registry. A trusted Verify-TiaMcpBundle.ps1 must accompany this script.
The expected worker digest must come from the verified release, not this proposal.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BundlePath,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedWorkerSha256,
    [string]$ReportPath,
    [switch]$InspectOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$applicationName = 'TiaMcp.Openness.V21.exe'
$entryPath = 'SOFTWARE\Siemens\Automation\Openness\AllowList\TiaMcp.Openness.V21.exe\Entry'
$sourceUrl = 'https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/tia-portal-openness-firewall'

function Assert-LocalPath {
    param([string]$Path)
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path -match '[\x00-\x1f"<>|?*]' -or $Path.Substring(2).Contains(':') -or
        @($Path.Substring(3) -split '[\\/]' | Where-Object { $_ -eq '.' -or $_ -eq '..' -or $_ -match '[. ]$' }).Count -gt 0) {
        throw 'Paths must be absolute local Windows drive paths, with no traversal, device/UNC syntax, stream or trailing-dot/space component.'
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    $ancestor = $full
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Paths must not traverse reparse points.' }
        }
        $parent = [IO.Directory]::GetParent($ancestor)
        $ancestor = if ($parent) { $parent.FullName } else { $null }
    }
    return $full
}

function Read-Entry {
    param($BaseKey)
    $key = $BaseKey.OpenSubKey($entryPath, $false)
    if ($null -eq $key) { return [pscustomobject]@{status='missing'; existed=$false; values=@()} }
    try {
        $values = @($key.GetValueNames() | Sort-Object | ForEach-Object {
            [pscustomobject]@{name=$_;kind=$key.GetValueKind($_).ToString();value=$key.GetValue($_,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}
        })
        return [pscustomobject]@{status='read';existed=$true;values=$values}
    }
    finally { $key.Dispose() }
}

function Save-RegistrationReport {
    if ($reportFile) {
        [IO.File]::WriteAllText($reportFile, ($report | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Registration inspection requires 64-bit Windows.'
}
if (-not $InspectOnly) {
    if (-not $ReportPath) { throw 'Apply mode requires an explicit fresh ReportPath outside the bundle to preserve recovery evidence.' }
    if (-not [Environment]::Is64BitProcess) { throw 'Use elevated 64-bit Windows PowerShell for registration.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Apply mode requires an elevated administrator token.' }
    }
    finally { $identity.Dispose() }
}
$bundleRoot = Assert-LocalPath $BundlePath
if (-not (Test-Path -LiteralPath $bundleRoot -PathType Container)) { throw 'BundlePath must be an existing directory.' }
$verifier = Join-Path $PSScriptRoot 'Verify-TiaMcpBundle.ps1'
if (-not (Test-Path -LiteralPath $verifier -PathType Leaf)) { throw 'Trusted sibling Verify-TiaMcpBundle.ps1 is required.' }
$integrity = & $verifier -BundlePath $bundleRoot -Strict
if (-not $integrity.verified -or -not $integrity.strict) { throw 'Strict bundle verification did not succeed.' }
$workerPath = Assert-LocalPath (Join-Path $bundleRoot ('workers/openness/' + $applicationName))
$prefix = $bundleRoot + [IO.Path]::DirectorySeparatorChar
if (-not $workerPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Worker path escaped the verified bundle.' }

$reportFile = $null
if ($ReportPath) { $reportFile = Assert-LocalPath $ReportPath }
if ($reportFile) {
    if ([IO.Path]::GetExtension($reportFile) -ine '.json' -or $reportFile.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ReportPath must be a JSON file outside the verified bundle.'
    }
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($reportFile))) { throw 'Report parent directory must already exist.' }
    if (Test-Path -LiteralPath $reportFile) { throw 'ReportPath already exists; use a new report so previous recovery evidence is preserved.' }
}

$stream = $null
$baseKey = $null
$writeKey = $null
$report = $null
try {
    # Prevent replacement/modification of the approved executable while hashing and registering.
    $stream = [IO.File]::Open($workerPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $digest = $algorithm.ComputeHash($stream) }
    finally { $algorithm.Dispose() }
    $actualHash = [BitConverter]::ToString($digest).Replace('-','').ToLowerInvariant()
    if ($actualHash -cne $ExpectedWorkerSha256.ToLowerInvariant()) { throw 'Worker SHA-256 differs from the explicitly expected release hash; no registration was written.' }
    $info = Get-Item -LiteralPath $workerPath -Force
    $proposal = [ordered]@{
        Path=$info.FullName
        DateModified=$info.LastWriteTimeUtc.ToString('yyyy/MM/dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)
        FileHash=[Convert]::ToBase64String($digest)
    }
    $previous = $null
    try {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
        $previous = Read-Entry $baseKey
    }
    catch {
        if (-not $InspectOnly) { throw }
        $previous = [pscustomobject]@{status='unavailable';existed=$null;values=@();reason='Current token cannot inspect this registry entry.'}
    }
    $report = [ordered]@{
        schemaVersion=1;capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');mode=$(if ($InspectOnly) {'inspect'} else {'apply'})
        status='prepared';registryWritesAttempted=$false;registrationWritten=$false;readBackVerified=$false
        bundlePath=$bundleRoot;strictBundleVerification=$integrity;workerSha256=$actualHash;expectedWorkerSha256=$ExpectedWorkerSha256.ToLowerInvariant()
        applicationName=$applicationName;registryHive='HKEY_LOCAL_MACHINE';registryView='Registry64';registryEntry=$entryPath;valueKind='String'
        proposedValues=$proposal;previousEntry=$previous;resultEntry=$null;reportPath=$reportFile;sourceUrl=$sourceUrl
        changes='Only Path, DateModified and FileHash in this application Entry. No other allowlist entries, Windows Firewall settings, group memberships, files in the bundle or Siemens processes are changed.'
        recovery='Previous Entry existence, value names, registry kinds and raw values are recorded. Restore only the three affected values from that evidence, preserving unrelated values; remove newly added values when absent previously. If the Entry did not exist, remove it only if its current values still match this registration and it has no unrelated content.'
    }
    if ($InspectOnly) {
        $report.status='inspected'
        Save-RegistrationReport
        Write-Output ($report | ConvertTo-Json -Depth 12)
        return
    }
    # Persist recovery evidence before the first registry mutation. Failed saves abort the change.
    Save-RegistrationReport
    $freshPrevious = Read-Entry $baseKey
    if (($freshPrevious | ConvertTo-Json -Depth 8 -Compress) -cne ($previous | ConvertTo-Json -Depth 8 -Compress)) {
        throw 'AllowList Entry changed during preparation; retry after reviewing the new state.'
    }
    $report.registryWritesAttempted=$true
    Save-RegistrationReport
    $writeKey = $baseKey.CreateSubKey($entryPath, $true)
    foreach ($name in @('Path','DateModified','FileHash')) { $writeKey.SetValue($name, $proposal[$name], [Microsoft.Win32.RegistryValueKind]::String) }
    $writeKey.Flush()
    $writeKey.Dispose(); $writeKey=$null
    $report.registrationWritten=$true
    $result = Read-Entry $baseKey
    foreach ($name in @('Path','DateModified','FileHash')) {
        $match = @($result.values | Where-Object name -IEQ $name)
        if ($match.Count -ne 1 -or $match[0].kind -cne 'String' -or $match[0].value -cne $proposal[$name]) { throw ('Registry readback did not match the proposed ' + $name + ' value.') }
    }
    $report.resultEntry=$result
    $report.readBackVerified=$true
    $report.status='registered'
    Save-RegistrationReport
    Write-Output ($report | ConvertTo-Json -Depth 12)
}
catch {
    if ($null -ne $report) {
        $report.status='failed'
        $report['error']=$_.Exception.Message
        Save-RegistrationReport
    }
    throw
}
finally {
    if ($null -ne $writeKey) { $writeKey.Dispose() }
    if ($null -ne $baseKey) { $baseKey.Dispose() }
    if ($null -ne $stream) { $stream.Dispose() }
}
