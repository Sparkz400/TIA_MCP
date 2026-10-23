[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][int]$NativeProcessId,
    [Parameter(Mandatory=$true)][string]$OpenObservationEvidence,
    [Parameter(Mandatory=$true)][string]$ResourceAdmissionEvidence,
    [Parameter(Mandatory=$true)][string]$CheckpointRetrievalEvidence,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$AdmissionPath,
    [Parameter(Mandatory=$true)][string]$PackageManifest,
    [string]$ArtifactRoot = 'D:\TIAW'
)
$ErrorActionPreference = 'Stop'
function Get-ExactArtifact([string]$ArtifactPath) {
    $item = Get-Item -LiteralPath $ArtifactPath
    if ($item.PSIsContainer) { throw 'Evidence must be a retained file.' }
    return [ordered]@{ path=$item.FullName; sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes=$item.Length }
}
$package = Get-Content -LiteralPath $PackageManifest -Raw | ConvertFrom-Json
foreach ($item in $package.admissionArtifacts) {
    if ((Get-ExactArtifact $item.path).sha256 -ne $item.sha256) { throw "Frozen package artifact changed: $($item.path)" }
}
if (Test-Path -LiteralPath $AdmissionPath) { throw 'A fresh admission filename is required.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'A fresh native evidence directory is required.' }
$process = Get-Process -Id $NativeProcessId
$admission = [ordered]@{
    mode='inspect-system-scope'; ownedScratch=$true; allProjectObjectsOwned=$true
    sequentialNativeAdmission=$true; checkpointRetrievalGatePassed=$true; systemCaseName='SingleStep'
    artifactRoot=[IO.Path]::GetFullPath($ArtifactRoot); outputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
    processId=$NativeProcessId; nativeProcessStartTimeUtc=$process.StartTime.ToUniversalTime().ToString('o')
    projectPath=$package.projectPath; binarySha256=$package.binary.sha256; apiSha256=$package.apiSha256
    ownershipEvidence=(Get-ExactArtifact $package.ownershipEvidence.path)
    openObservationEvidence=(Get-ExactArtifact $OpenObservationEvidence)
    resourceAdmissionEvidence=(Get-ExactArtifact $ResourceAdmissionEvidence)
    checkpointRetrievalEvidence=(Get-ExactArtifact $CheckpointRetrievalEvidence)
    reviewedSource=(Get-ExactArtifact $package.reviewedSource.path)
    sourceFiles=$package.sourceFiles
}
# This only writes the concrete request. It does not admit resources or invoke TIA.
$fullAdmission = [IO.Path]::GetFullPath($AdmissionPath)
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($admission | ConvertTo-Json -Depth 12))
$stream = [IO.FileStream]::new($fullAdmission, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
[ordered]@{ admissionPath=$fullAdmission; binaryPath=$package.binary.path; nativeInvoked=$false; callerMustHoldSequentialNativeAdmission=$true } | ConvertTo-Json
