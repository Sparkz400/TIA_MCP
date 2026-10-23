#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BundleZip,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedSha256,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [string]$PrerequisitesManifest,
    [switch]$Engineering
)
$ErrorActionPreference = 'Stop'
if ($EvidenceDirectory -notmatch '^[A-Za-z]:[\\/]' -or $EvidenceDirectory -match '[\x00-\x1f"<>|?*]' -or $EvidenceDirectory.Substring(2).Contains(':')) {
    throw 'EvidenceDirectory must be an absolute local Windows drive path, not a share or device path.'
}
$evidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
$ancestor = $evidenceRoot
while ($ancestor) {
    if (Test-Path -LiteralPath $ancestor) {
        if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'EvidenceDirectory must not traverse a reparse point.' }
    }
    $parentDirectory = [IO.Directory]::GetParent($ancestor)
    $ancestor = if ($parentDirectory) { $parentDirectory.FullName } else { $null }
}
$runDirectory = Join-Path $evidenceRoot ('run_' + [Guid]::NewGuid().ToString('N').Substring(0,12))
[IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
if (Test-Path -LiteralPath $runDirectory) { throw 'Generated evidence directory already exists.' }
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$reportPath = Join-Path $runDirectory 'guest-summary.json'
$report = [ordered]@{
    schemaVersion=1; startedAtUtc=[DateTime]::UtcNow.ToString('O'); completed=$false; status='running'
    executionMachine=[ordered]@{computerName=[Environment]::MachineName; osVersion=[Environment]::OSVersion.VersionString; is64BitOperatingSystem=[Environment]::Is64BitOperatingSystem; is64BitProcess=[Environment]::Is64BitProcess; powershellVersion=$PSVersionTable.PSVersion.ToString(); virtualization='unverified'}
    expectedArchiveSha256=$ExpectedSha256.ToLowerInvariant(); actualArchiveSha256=$null
    evidenceDirectory=$runDirectory; engineeringRequested=[bool]$Engineering; settingsModified=$false
    prerequisiteMode=$(if ($PrerequisitesManifest) {'portable-owned-directory'} else {'existing-installations'})
    stages=New-Object System.Collections.ArrayList
    limits=@{maxArchiveBytes=536870912; maxEntries=5000; maxExpandedBytes=2147483648; maxEntryBytes=268435456}
}
function Save-GuestReport { [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 14), (New-Object Text.UTF8Encoding($false))) }
function Add-GuestStage([string]$Name, [string]$Status, $Details) {
    [void]$report.stages.Add([ordered]@{name=$Name;status=$Status;details=$Details;observedAtUtc=[DateTime]::UtcNow.ToString('O')})
    Save-GuestReport
}
function Get-StreamSha256($Stream) {
    $hashAlgorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hashAlgorithm.ComputeHash($Stream)).Replace('-','').ToLowerInvariant() }
    finally { $hashAlgorithm.Dispose() }
}
function Expand-PortableArchive([string]$ArchivePath, [string]$Sha256, [string]$DestinationRoot) {
    $sourceStream = [IO.File]::Open($ArchivePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $portableZip = $null
    try {
        if ($sourceStream.Length -gt $report.limits.maxArchiveBytes) { throw 'Portable archive exceeds 512 MiB.' }
        $actual = Get-StreamSha256 $sourceStream
        if ($actual -cne $Sha256.ToLowerInvariant()) { throw 'Portable prerequisite SHA-256 mismatch; package was not extracted or executed.' }
        $sourceStream.Position = 0
        $portableZip = New-Object IO.Compression.ZipArchive($sourceStream, [IO.Compression.ZipArchiveMode]::Read, $true)
        if ($portableZip.Entries.Count -eq 0 -or $portableZip.Entries.Count -gt $report.limits.maxEntries) { throw 'Portable archive entry count is outside 1..5000.' }
        $destinationPrefix = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
        if (-not $destinationPrefix.StartsWith(([IO.Path]::GetFullPath((Join-Path $runDirectory 'tools')) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw 'Portable tool extraction target escaped its owned run directory.' }
        $seenEntries = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        $portableEntries = New-Object System.Collections.ArrayList
        [long]$expandedBytes = 0
        foreach ($entry in $portableZip.Entries) {
            $normalized = $entry.FullName.Replace('\','/')
            $relative = $normalized.TrimEnd('/')
            if (-not $relative -or $relative -match '^/' -or $relative -match '[:\x00-\x1f"<>|?*]' -or
                @($relative -split '/' | Where-Object { -not $_ -or $_ -eq '.' -or $_ -eq '..' -or $_ -match '[. ]$' }).Count -gt 0 -or -not $seenEntries.Add($relative)) { throw 'Portable archive contains an unsafe or duplicate Windows path.' }
            if (($entry.ExternalAttributes -band 0x400) -ne 0 -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Portable archive contains a link/reparse entry.' }
            if ($entry.Length -gt $report.limits.maxEntryBytes) { throw 'Portable archive entry exceeds 256 MiB.' }
            $expandedBytes += $entry.Length
            if ($expandedBytes -gt $report.limits.maxExpandedBytes) { throw 'Portable archive expanded size exceeds 2 GiB.' }
            $destination = [IO.Path]::GetFullPath((Join-Path $DestinationRoot $relative))
            if (-not $destination.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Portable archive path escaped its extraction directory.' }
            [void]$portableEntries.Add(@{entry=$entry;destination=$destination;directory=$normalized.EndsWith('/')})
        }
        $identicalDuplicates = 0
        foreach ($item in $portableEntries) {
            $checkPath = $item.destination
            while ($checkPath -and $checkPath.StartsWith([IO.Path]::GetFullPath($runDirectory), [StringComparison]::OrdinalIgnoreCase)) {
                if (Test-Path -LiteralPath $checkPath) {
                    if ((Get-Item -LiteralPath $checkPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Portable extraction refuses reparse points.' }
                }
                $parentInfo = [IO.Directory]::GetParent($checkPath)
                $checkPath = if ($parentInfo) {$parentInfo.FullName} else {$null}
            }
            if ($item.directory) { [IO.Directory]::CreateDirectory($item.destination) | Out-Null; continue }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($item.destination)) | Out-Null
            $entryStream = $item.entry.Open()
            $targetStream = $null
            try {
                if (Test-Path -LiteralPath $item.destination) {
                    $existing = [IO.File]::OpenRead($item.destination)
                    try {
                        if ($existing.Length -ne $item.entry.Length -or (Get-StreamSha256 $existing) -cne (Get-StreamSha256 $entryStream)) { throw 'Portable archives contain conflicting files; no existing file was overwritten.' }
                    } finally { $existing.Dispose() }
                    $identicalDuplicates++
                    continue
                }
                $targetStream = [IO.File]::Open($item.destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                $buffer = New-Object byte[] 65536
                [long]$copied = 0
                while (($read = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $copied += $read
                    if ($copied -gt $item.entry.Length -or $copied -gt $report.limits.maxEntryBytes) { throw 'Portable entry expanded beyond its declared size.' }
                    $targetStream.Write($buffer, 0, $read)
                }
                if ($copied -ne $item.entry.Length) { throw 'Portable entry decompressed length mismatch.' }
            } finally { if ($targetStream) {$targetStream.Dispose()}; $entryStream.Dispose() }
        }
        return @{sha256=$actual;entries=$portableEntries.Count;expandedBytes=$expandedBytes;identicalDuplicates=$identicalDuplicates;destination=$DestinationRoot}
    } finally { if ($portableZip) {$portableZip.Dispose()}; $sourceStream.Dispose() }
}
Save-GuestReport
$archiveStream = $null
$zip = $null
$currentStage = 'archive_validation'
$portableDotnetRoot = $null
$portablePython = $null
$originalProcessEnvironment = @{PATH=$env:PATH; DOTNET_ROOT=$env:DOTNET_ROOT; DOTNET_ROOT_X64=$env:DOTNET_ROOT_X64}
try {
    # This process runs on whichever machine the operator selected. Record actual
    # machine metadata, never infer VMware placement from a target label.
    try {
        $computer = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
        $report.executionMachine.manufacturer = $computer.Manufacturer
        $report.executionMachine.model = $computer.Model
        if ($computer.Manufacturer -match 'VMware' -or $computer.Model -match 'VMware') { $report.executionMachine.virtualization = 'VMware model observed on execution machine' }
    } catch { $report.executionMachine.machineModelRead = 'unavailable' }
    $archivePath = [IO.Path]::GetFullPath($BundleZip)
    $archiveStream = [IO.File]::Open($archivePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    if ($archiveStream.Length -gt $report.limits.maxArchiveBytes) { throw 'Archive exceeds the 512 MiB qualification limit.' }
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $actualHash = [BitConverter]::ToString($algorithm.ComputeHash($archiveStream)).Replace('-','').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
    $report.actualArchiveSha256 = $actualHash
    if ($actualHash -cne $ExpectedSha256.ToLowerInvariant()) { throw 'Archive SHA-256 does not match the expected release hash; nothing was extracted or executed.' }
    $archiveStream.Position = 0
    Add-Type -AssemblyName System.IO.Compression
    $zip = New-Object IO.Compression.ZipArchive($archiveStream, [IO.Compression.ZipArchiveMode]::Read, $true)
    if ($zip.Entries.Count -eq 0 -or $zip.Entries.Count -gt $report.limits.maxEntries) { throw 'Archive entry count is outside 1..5000.' }
    $entries = New-Object System.Collections.ArrayList
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    [long]$expandedBytes = 0
    $bundleRoot = Join-Path $runDirectory 'bundle'
    $bundlePrefix = $bundleRoot + [IO.Path]::DirectorySeparatorChar
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName.Replace('\','/')
        $isDirectory = $name.EndsWith('/')
        $relative = $name.TrimEnd('/')
        if (-not $relative -or $relative -match '^/' -or $relative -match '[:\x00-\x1f"<>|?*]' -or
            @($relative -split '/' | Where-Object { -not $_ -or $_ -eq '.' -or $_ -eq '..' -or $_ -match '[. ]$' }).Count -gt 0 -or -not $seen.Add($relative)) { throw 'Archive contains an unsafe or duplicate Windows path.' }
        # Unix symlink mode and DOS reparse-point attributes are both rejected.
        if (($entry.ExternalAttributes -band 0x400) -ne 0 -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Archive contains a link/reparse entry.' }
        $destination = [IO.Path]::GetFullPath((Join-Path $bundleRoot $relative))
        if (-not $destination.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path escapes the owned extraction directory.' }
        if ($entry.Length -gt $report.limits.maxEntryBytes) { throw 'Archive entry exceeds the 256 MiB limit.' }
        $expandedBytes += $entry.Length
        if ($expandedBytes -gt $report.limits.maxExpandedBytes) { throw 'Archive expanded size exceeds 2 GiB.' }
        [void]$entries.Add(@{entry=$entry; destination=$destination; directory=$isDirectory})
    }
    Add-GuestStage 'archive_validation' 'passed' @{sha256=$actualHash;entries=$entries.Count;expandedBytes=$expandedBytes}
    $currentStage = 'extract_owned_bundle'
    [IO.Directory]::CreateDirectory($bundleRoot) | Out-Null
    foreach ($item in $entries) {
        if ($item.directory) { [IO.Directory]::CreateDirectory($item.destination) | Out-Null; continue }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($item.destination)) | Out-Null
        $inputStream = $item.entry.Open()
        $outputStream = [IO.File]::Open($item.destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $inputStream.CopyTo($outputStream) }
        finally { $outputStream.Dispose(); $inputStream.Dispose() }
    }
    $zip.Dispose(); $zip = $null
    $archiveStream.Dispose(); $archiveStream = $null
    Add-GuestStage 'extract_owned_bundle' 'passed' @{bundlePath=$bundleRoot}
    $currentStage = 'bundle_integrity'
    $verified = & (Join-Path $bundleRoot 'scripts/Verify-TiaMcpBundle.ps1') -BundlePath $bundleRoot -Strict
    if (-not $verified.verified) { throw 'Extracted bundle did not confirm integrity.' }
    Add-GuestStage 'bundle_integrity' 'passed' $verified
    if ($PrerequisitesManifest) {
        $currentStage = 'portable_prerequisites'
        $manifestPath = [IO.Path]::GetFullPath($PrerequisitesManifest)
        $manifestDirectory = [IO.Path]::GetDirectoryName($manifestPath)
        $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
        if ($manifestBytes.Length -gt 1048576) { throw 'Prerequisite manifest exceeds 1 MiB.' }
        $manifestText = [Text.Encoding]::UTF8.GetString($manifestBytes).TrimStart([char]0xfeff)
        $manifest = $manifestText | ConvertFrom-Json
        if ($manifest.schemaVersion -ne 1 -or $manifest.packages -isnot [Array] -or $manifest.packages.Count -ne 3) { throw 'Prerequisites manifest must declare exactly three schema-v1 packages.' }
        $packageIds = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($package in $manifest.packages) {
            if ($package.id -notin @('dotnet-runtime','aspnet-runtime','python') -or -not $packageIds.Add($package.id) -or
                $package.file -isnot [string] -or $package.file -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_.-]*\.zip$' -or
                $package.sha256 -isnot [string] -or $package.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $package.architecture -ne 'x64') { throw 'Invalid, duplicated or unsupported prerequisite package metadata.' }
        }
        $manifestMemory = New-Object IO.MemoryStream(,$manifestBytes)
        try { $manifestHash = Get-StreamSha256 $manifestMemory } finally {$manifestMemory.Dispose()}
        $report.portablePrerequisites = @{manifestPath=$manifestPath;manifestSha256=$manifestHash;packages=New-Object System.Collections.ArrayList;machineInstallationModified=$false}
        foreach ($package in $manifest.packages) {
            $destination = Join-Path $runDirectory $(if ($package.id -eq 'python') {'tools/python'} else {'tools/dotnet'})
            $extracted = Expand-PortableArchive (Join-Path $manifestDirectory $package.file) $package.sha256 $destination
            [void]$report.portablePrerequisites.packages.Add(@{id=$package.id;file=$package.file;version=$package.version;architecture=$package.architecture;sourceUrl=$package.sourceUrl;publisherHashEvidence=$package.verifiedPublisherHash;extraction=$extracted})
            Save-GuestReport
        }
        $portableDotnetRoot = Join-Path $runDirectory 'tools/dotnet'
        $portablePython = Join-Path $runDirectory 'tools/python/python.exe'
        if (-not (Test-Path -LiteralPath (Join-Path $portableDotnetRoot 'dotnet.exe') -PathType Leaf) -or -not (Test-Path -LiteralPath $portablePython -PathType Leaf)) { throw 'Portable archives omitted their expected x64 executables.' }
        $env:DOTNET_ROOT_X64 = $portableDotnetRoot
        $env:DOTNET_ROOT = $portableDotnetRoot
        $env:PATH = $portableDotnetRoot + ';' + [IO.Path]::GetDirectoryName($portablePython) + ';' + $originalProcessEnvironment.PATH
        Add-GuestStage 'portable_prerequisites' 'passed' $report.portablePrerequisites
    }
    $currentStage = 'environment_diagnostics'
    $doctorPath = Join-Path $runDirectory 'environment.json'
    $doctorArguments = @{BundlePath=$bundleRoot;OutputPath=$doctorPath}
    if ($portableDotnetRoot) {$doctorArguments.DotnetRoot = $portableDotnetRoot}
    $doctorJson = & (Join-Path $bundleRoot 'scripts/Test-TiaMcpEnvironment.ps1') @doctorArguments
    $doctor = $doctorJson | ConvertFrom-Json
    Add-GuestStage 'environment_diagnostics' 'passed' @{report=$doctorPath;hostPrerequisites=$doctor.capabilities.host;offlinePrerequisites=$doctor.capabilities.offline}
    $currentStage = 'configuration'
    $configPath = Join-Path $runDirectory 'mcp.local.json'
    $artifactsPath = Join-Path $runDirectory 'artifacts'
    & (Join-Path $bundleRoot 'scripts/New-McpConfiguration.ps1') -BundlePath $bundleRoot -ArtifactsDirectory $artifactsPath -TimeoutSeconds 300 -StrictBundle |
        Set-Content -LiteralPath $configPath -Encoding UTF8
    if ($portableDotnetRoot) {
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $config.mcpServers.'tia-v21' | Add-Member -NotePropertyName env -NotePropertyValue @{DOTNET_ROOT_X64=$portableDotnetRoot;DOTNET_ROOT=$portableDotnetRoot}
        $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding UTF8
    }
    Add-GuestStage 'configuration' 'passed' @{configurationPath=$configPath;artifactDirectory=$artifactsPath;insideBundle=$false}
    $currentStage = 'python_prerequisite'
    $python = $null
    $pythonPrefix = @()
    $pythonVersion = $null
    $pythonCandidates = if ($portablePython) {@($portablePython)} else {@('python.exe','python3.exe','py.exe')}
    foreach ($candidateName in $pythonCandidates) {
        $candidate = Get-Command $candidateName -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $candidate -or $candidate.Source -match '[\\/]Microsoft[\\/]WindowsApps[\\/]') { continue }
        $prefix = if ($candidateName -eq 'py.exe') {@('-3')} else {@()}
        try {
            $versionText = & $candidate.Source @prefix -c 'import sys; print(*sys.version_info[:3], sep=chr(46)); sys.exit(0 if sys.version_info >= (3,10) else 3)' 2>$null
            if ($LASTEXITCODE -eq 0 -and $versionText -match '^3\.[0-9]+\.[0-9]+$') { $python=$candidate.Source; $pythonPrefix=$prefix; $pythonVersion=$versionText; break }
        } catch { }
    }
    if (-not $python) { Add-GuestStage 'python_prerequisite' 'blocked' 'Python 3.10+ was not found in the current PATH; no interpreter was installed.' }
    else { Add-GuestStage 'python_prerequisite' 'passed' @{path=$python;version=$pythonVersion} }
    $currentStage = 'mcp_qualification'
    if ($doctor.capabilities.host.status -ne 'present') {
        Add-GuestStage 'mcp_qualification' 'blocked' @{reason='Host runtime prerequisites were not established; see environment.json';hostPrerequisites=$doctor.capabilities.host}
    } elseif (-not $python) { Add-GuestStage 'mcp_qualification' 'blocked' 'Python prerequisite is blocked.' }
    else {
        $qualificationPath = Join-Path $runDirectory 'qualification.json'
        $arguments = @($pythonPrefix) + @('-B', (Join-Path $bundleRoot 'scripts/qualify_deployment.py'), '--config', $configPath, '--report', $qualificationPath, '--timeout', '600', '--target-label', [Environment]::MachineName)
        if ($Engineering) { $arguments += '--engineering' }
        $qualificationOutput = & $python @arguments 2>&1
        $qualificationExit = $LASTEXITCODE
        [IO.File]::WriteAllLines((Join-Path $runDirectory 'qualification-console.log'), @($qualificationOutput | ForEach-Object { $_.ToString() }))
        if (-not (Test-Path -LiteralPath $qualificationPath -PathType Leaf)) { throw 'Qualification process did not produce its result file.' }
        $qualification = Get-Content -LiteralPath $qualificationPath -Raw | ConvertFrom-Json
        $qualificationStatus = if ($qualificationExit -eq 0 -and $qualification.completed -eq $true -and $qualification.status -eq 'passed') {'passed'} elseif ($qualificationExit -eq 2 -and $qualification.status -eq 'blocked') {'blocked'} else {'failed'}
        Add-GuestStage 'mcp_qualification' $qualificationStatus @{report=$qualificationPath;exitCode=$qualificationExit;engineeringRequested=[bool]$Engineering;transport=$qualification.transport}
    }
} catch {
    Add-GuestStage $currentStage 'failed' $_.Exception.Message
} finally {
    $env:PATH = $originalProcessEnvironment.PATH
    $env:DOTNET_ROOT = $originalProcessEnvironment.DOTNET_ROOT
    $env:DOTNET_ROOT_X64 = $originalProcessEnvironment.DOTNET_ROOT_X64
    if ($zip) { $zip.Dispose() }
    if ($archiveStream) { $archiveStream.Dispose() }
    $statuses = @($report.stages | ForEach-Object { $_.status })
    $report.status = if ($statuses -contains 'failed') {'failed'} elseif ($statuses -contains 'blocked') {'blocked'} else {'passed'}
    $report.completed = $report.status -eq 'passed'
    $report.finishedAtUtc = [DateTime]::UtcNow.ToString('O')
    Save-GuestReport
}
[pscustomobject]@{status=$report.status;completed=$report.completed;reportPath=$reportPath;evidenceDirectory=$runDirectory} | ConvertTo-Json -Compress
if ($report.status -eq 'passed') { exit 0 }
if ($report.status -eq 'blocked') { exit 2 }
exit 1
