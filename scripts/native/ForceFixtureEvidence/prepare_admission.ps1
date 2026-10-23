[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('metadata','author-ui','capture')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$ProjectPath,
    [Parameter(Mandatory=$true)][string]$OwnershipEvidence,
    [Parameter(Mandatory=$true)][string]$ReviewedCheckpoint,
    [Parameter(Mandatory=$true)][string]$OraclePath,
    [Parameter(Mandatory=$true)][string]$ResourceAdmissionEvidence,
    [Parameter(Mandatory=$true)][string]$CheckpointRetrievalEvidence,
    [Parameter(Mandatory=$true)][string]$ArtifactRoot,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$AdmissionPath,
    [Parameter(Mandatory=$true)][string]$PackageManifest
)
$ErrorActionPreference = 'Stop'
function Describe([string]$ArtifactPath) {
    $item=Get-Item -LiteralPath $ArtifactPath
    if ($item.PSIsContainer) { throw 'Expected retained file.' }
    return [ordered]@{path=$item.FullName;sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$item.Length}
}
$package=Get-Content -LiteralPath $PackageManifest -Raw | ConvertFrom-Json
foreach ($item in $package.admissionArtifacts) {
    if ((Describe $item.path).sha256 -ne $item.sha256) { throw "Frozen artifact changed: $($item.path)" }
}
if ((Test-Path -LiteralPath $OutputDirectory) -or (Test-Path -LiteralPath $AdmissionPath)) { throw 'Fresh output and admission paths are required.' }
$admission=[ordered]@{
    mode=$Mode;ownedScratch=$true;allProjectObjectsOwned=$true;sequentialNativeAdmission=$true;checkpointRetrievalGatePassed=$true
    projectPath=[IO.Path]::GetFullPath($ProjectPath);projectFileSha256=(Describe $ProjectPath).sha256
    artifactRoot=[IO.Path]::GetFullPath($ArtifactRoot);outputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
    binarySha256=$package.binary.sha256;apiSha256=$package.apiSha256;sourceFiles=$package.sourceFiles
    ownershipEvidence=(Describe $OwnershipEvidence);reviewedCheckpoint=(Describe $ReviewedCheckpoint);oracle=(Describe $OraclePath)
    resourceAdmissionEvidence=(Describe $ResourceAdmissionEvidence);checkpointRetrievalEvidence=(Describe $CheckpointRetrievalEvidence)
    uiTimeoutMinutes=30
}
# Writes the exact request only. This does not grant resource admission or launch TIA.
$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($admission | ConvertTo-Json -Depth 12))
$path=[IO.Path]::GetFullPath($AdmissionPath)
$file=[IO.FileStream]::new($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
try { $file.Write($bytes,0,$bytes.Length);$file.Flush($true) } finally { $file.Dispose() }
[ordered]@{admissionPath=$path;binaryPath=$package.binary.path;nativeInvoked=$false;callerMustHoldSequentialNativeAdmission=$true} | ConvertTo-Json
