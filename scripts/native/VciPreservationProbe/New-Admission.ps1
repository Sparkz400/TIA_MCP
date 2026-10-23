[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('export','snapshot')][string]$Mode,
    [Parameter(Mandatory)][int]$NativeProcessId,
    [Parameter(Mandatory)][string]$ProjectPath,
    [Parameter(Mandatory)][string]$PlcName,
    [Parameter(Mandatory)][string]$CheckpointPath,
    [Parameter(Mandatory)][string]$OraclePath,
    [Parameter(Mandatory)][string]$HostBinaryPath,
    [Parameter(Mandatory)][string]$WorkerBinaryPath,
    [Parameter(Mandatory)][string]$RecoveryFoundationEvidencePath,
    [Parameter(Mandatory)][switch]$RecoveryFoundationPassed,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$AdmissionPath,
    [string]$WorkspacePrefix = 'VCI_StartupUnblock01'
)
$ErrorActionPreference = 'Stop'
if (-not $RecoveryFoundationPassed) { throw 'Coordinator must verify actual checkpoint/retrieval foundation before admission.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$artifactRoot = Join-Path $repoRoot 'artifacts'
$collectorPath = Join-Path $artifactRoot 'startup-unblock-20261015/vci/build/bin/Release/net48/VciPreservationProbe.exe'
function Get-Artifact([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    if ($item.PSIsContainer) { throw 'Expected exact file.' }
    return [ordered]@{ path = $item.FullName; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = $item.Length }
}
$nativeProcess = Get-Process -Id $NativeProcessId
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Output already exists; reconcile its intent before another run.' }
if (Test-Path -LiteralPath $AdmissionPath) { throw 'Admission is immutable; reserve a new filename.' }
$admission = [ordered]@{
    mode = $Mode; binarySha256 = (Get-Artifact $collectorPath).sha256
    artifactRoot = $artifactRoot; outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    projectPath = [IO.Path]::GetFullPath($ProjectPath); plcName = $PlcName
    processId = $NativeProcessId; processStartedAtUtc = $nativeProcess.StartTime.ToUniversalTime().ToString('O')
    ownedScratch = $true; sequentialNativeAdmission = $true; checkpointRetrievalFoundationPassed = $true
    checkpoint = Get-Artifact $CheckpointPath; oracle = Get-Artifact $OraclePath
    hostBinary = Get-Artifact $HostBinaryPath; workerBinary = Get-Artifact $WorkerBinaryPath
    recoveryFoundationEvidence = Get-Artifact $RecoveryFoundationEvidencePath
    workspacePrefix = $WorkspacePrefix
    sourceFiles = @((Get-Artifact (Join-Path $PSScriptRoot 'Program.cs')), (Get-Artifact (Join-Path $PSScriptRoot 'VciPreservationProbe.csproj')))
    scope = 'Twelve VCI workspaces and two direct SD exports; no imports; no native qualification promotion'
}
$json = $admission | ConvertTo-Json -Depth 20
$stream = [IO.File]::Open([IO.Path]::GetFullPath($AdmissionPath), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
try {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush($true)
} finally { $stream.Dispose() }
Get-Artifact $AdmissionPath | ConvertTo-Json
