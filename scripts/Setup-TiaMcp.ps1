#Requires -Version 5.1
<#
.SYNOPSIS
Finish setup or an explicitly approved update at an engineering bundle's final location.
.DESCRIPTION
Registers the verified worker and writes a fresh external MCP configuration. Run
elevated for setup/update; InspectOnly needs no elevation and writes nothing.
Never starts TIA, modifies a project, changes group membership or grants ACLs.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BundlePath,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedWorkerSha256,
    [Parameter(Mandatory=$true)][string]$ArtifactsDirectory,
    [Parameter(Mandatory=$true)][string]$ConfigurationPath,
    [string]$ReportPath,
    [string]$TiaApiDirectory,
    [string]$SimulatorApiDirectory,
    [ValidatePattern('^[a-z]+(,[a-z]+)*$')][string]$Toolsets,
    [ValidateRange(5,900)][int]$TimeoutSeconds = 300,
    [switch]$InspectOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpennessRegistration.Helpers.ps1')
$bundle = Assert-OpennessLocalPath $BundlePath
$configurationFile = Assert-OpennessLocalPath $ConfigurationPath
$artifacts = Assert-OpennessLocalPath $ArtifactsDirectory
$prefix = $bundle + [IO.Path]::DirectorySeparatorChar
foreach ($path in @($configurationFile, $artifacts)) {
    if ($path.Equals($bundle,[StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Configuration and artifacts must be outside the immutable bundle.'
    }
}
if ((Test-Path -LiteralPath $artifacts -PathType Leaf) -or $artifacts.Equals($configurationFile,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'ArtifactsDirectory must be a directory path distinct from the configuration file.'
}
if ([IO.Path]::GetExtension($configurationFile) -ine '.json' -or (Test-Path -LiteralPath $configurationFile) -or
    -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($configurationFile))) {
    throw 'ConfigurationPath must be a fresh JSON file in an existing directory outside the bundle.'
}
if (-not $InspectOnly -and -not $ReportPath) { throw 'Setup/update requires a fresh registration ReportPath outside the bundle.' }
if ($ReportPath -and (Assert-OpennessLocalPath $ReportPath).Equals($configurationFile,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'ReportPath and ConfigurationPath must be different files.'
}
if ($ReportPath -and (Assert-OpennessLocalPath $ReportPath).Equals($artifacts,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'ReportPath must be distinct from ArtifactsDirectory.'
}
# Generate first so invalid configuration options fail before registration.
$configurationArgs = @{BundlePath=$bundle;ArtifactsDirectory=$artifacts;TimeoutSeconds=$TimeoutSeconds;StrictBundle=$true}
foreach ($name in @('TiaApiDirectory','SimulatorApiDirectory','Toolsets')) {
    if ($PSBoundParameters.ContainsKey($name)) { $configurationArgs[$name] = $PSBoundParameters[$name] }
}
$configuration = & (Join-Path $PSScriptRoot 'New-McpConfiguration.ps1') @configurationArgs
$registrationArgs = @{BundlePath=$bundle;ExpectedWorkerSha256=$ExpectedWorkerSha256;InspectOnly=$InspectOnly}
# Inspect is entirely read-only, including the proposed report and configuration.
if (-not $InspectOnly) { $registrationArgs.ReportPath = $ReportPath }
$registration = & (Join-Path $PSScriptRoot 'Register-TiaOpennessApplication.ps1') @registrationArgs | ConvertFrom-Json
if (-not $InspectOnly) {
    if (-not $registration.readBackVerified -or $registration.status -notin @('registered','already_registered')) {
        throw 'Setup did not verify registration; no client configuration was written.'
    }
    # Do not overwrite a file created concurrently or an existing client configuration.
    $stream = [IO.File]::Open($configurationFile,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {
        $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes(($configuration -join [Environment]::NewLine))
        $stream.Write($bytes,0,$bytes.Length)
    }
    finally { $stream.Dispose() }
}
[ordered]@{
    schemaVersion=1;status=$(if ($InspectOnly) {'inspected'} else {'configured'})
    bundlePath=$bundle;configurationPath=$configurationFile;configurationWritten=(-not $InspectOnly)
    configuration=($configuration | ConvertFrom-Json);registration=$registration
    nextStep='Import the external configuration into your MCP client. After a trusted update or relocation, run setup again with the new release hash and fresh output paths. Normal startup never registers executables.'
} | ConvertTo-Json -Depth 16
