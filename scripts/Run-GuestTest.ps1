#Requires -Version 5.1
# Run inside the Windows guest. The only host writes are selected diagnostic
# reports in this dedicated exchange share; project/archive data stays local.
$ErrorActionPreference = 'Stop'
$exchangeRoot = $PSScriptRoot
$releasePath = Join-Path $exchangeRoot 'release.json'
$release = Get-Content -LiteralPath $releasePath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($release.zip -isnot [string] -or $release.zip -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*\.zip$' -or
    $release.sha256 -isnot [string] -or $release.sha256 -notmatch '^[a-fA-F0-9]{64}$') {
    throw 'release.json must contain one ZIP basename and its exact SHA-256.'
}
$bootstrap = Join-Path $exchangeRoot 'Invoke-GuestQualification.ps1'
$archive = Join-Path $exchangeRoot $release.zip
$guestEvidenceRoot = 'C:\TiaEvidence'
# Native PowerShell -File receives separate literal arguments; no shell-built
# command includes the selected path or hash.
$bootstrapArguments = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','RemoteSigned','-File',$bootstrap,'-BundleZip',$archive,'-ExpectedSha256',$release.sha256,'-EvidenceDirectory',$guestEvidenceRoot,'-Engineering')
$prerequisites = Join-Path $exchangeRoot 'prerequisites/manifest.json'
if (Test-Path -LiteralPath $prerequisites -PathType Leaf) { $bootstrapArguments += @('-PrerequisitesManifest',$prerequisites) }
$bootstrapOutput = & powershell.exe @bootstrapArguments
$bootstrapExit = $LASTEXITCODE
try { $summary = ($bootstrapOutput -join [Environment]::NewLine) | ConvertFrom-Json }
catch { throw 'Guest bootstrap did not return its final JSON summary. Review its console error; no host files were copied.' }
if ($summary.evidenceDirectory -isnot [string] -or $summary.reportPath -isnot [string]) { throw 'Guest bootstrap omitted its exact evidence paths.' }
$runDirectory = [IO.Path]::GetFullPath($summary.evidenceDirectory)
$expectedPrefix = [IO.Path]::GetFullPath($guestEvidenceRoot).TrimEnd('\') + '\'
if (-not $runDirectory.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $runDirectory.Substring($expectedPrefix.Length) -notmatch '^run_[a-f0-9]{12}$' -or
    -not ([IO.Path]::GetFullPath($summary.reportPath)).Equals((Join-Path $runDirectory 'guest-summary.json'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Guest bootstrap returned an unexpected evidence path; no host files were copied.'
}
$ancestor = $runDirectory
while ($ancestor) {
    $item = Get-Item -LiteralPath $ancestor -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Evidence path traverses a reparse point; no reports were copied.' }
    $parentDirectory = [IO.Directory]::GetParent($ancestor)
    $ancestor = if ($parentDirectory) { $parentDirectory.FullName } else { $null }
}
$candidates = @()
foreach ($name in @('guest-summary.json','environment.json','qualification.json','qualification-console.log')) {
    $source = Join-Path $runDirectory $name
    if (Test-Path -LiteralPath $source -PathType Leaf) {
        $file = Get-Item -LiteralPath $source -Force
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint -or $file.Length -gt 8MB) { throw 'A returned report is a link or exceeds 8 MiB; no reports were copied.' }
        $candidates += @{name=$name;source=$source}
    }
}
if (@($candidates | Where-Object { $_.name -eq 'guest-summary.json' }).Count -ne 1) { throw 'Guest summary file is missing; no reports were copied.' }
$resultsRoot = Join-Path $exchangeRoot 'results'
$destination = Join-Path $resultsRoot ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($destination) | Out-Null
foreach ($candidate in $candidates) { [IO.File]::Copy($candidate.source, (Join-Path $destination $candidate.name), $false) }
[pscustomobject]@{status=$summary.status;completed=$summary.completed;bootstrapExitCode=$bootstrapExit;guestEvidenceDirectory=$runDirectory;reportsReturnedToHost=$destination;copiedFiles=@($candidates | ForEach-Object { $_.name })} | ConvertTo-Json -Depth 4
exit $bootstrapExit
