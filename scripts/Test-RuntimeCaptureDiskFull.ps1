param(
    [Parameter(Mandatory=$true)][string]$TestAssembly,
    [string]$OutputDirectory = 'artifacts/runtime-disk-full'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Disk fixture must live below this repository artifacts directory.'
}
$assembly = (Resolve-Path -LiteralPath $TestAssembly).Path
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory to retain every prior result.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$diskPath = Join-Path $output 'bounded-64m.vhdx'
$token = [Guid]::NewGuid().ToString('N')
$receipt = [ordered]@{schemaVersion='runtime-disk-full-run.v1';diskPath=$diskPath;sizeBytes=67108864;token=$token;actualDiskFull=$false;status='preparing';nativeExecuted=$false}
$mounted = $false
$diskNumber = $null
$diskIdentity = $null
$testFinished = $true
$failure = $null
try {
    New-VHD -Path $diskPath -SizeBytes 67108864 -Fixed | Out-Null
    $owned = Get-VHD -Path $diskPath
    if ($owned.Path -ne $diskPath -or $owned.Attached -or $owned.Size -ne 67108864 -or $owned.VhdType -ne 'Fixed') { throw 'Owned VHD identity mismatch before mount.' }
    Mount-VHD -Path $diskPath -NoDriveLetter
    $mounted = $true
    $owned = Get-VHD -Path $diskPath
    $ownedDisk = Get-Disk -Number $owned.DiskNumber
    if ($owned.Path -ne $diskPath -or -not $owned.Attached -or $ownedDisk.Size -ne 67108864 -or $ownedDisk.IsBoot -or $ownedDisk.IsSystem -or $ownedDisk.PartitionStyle -ne 'RAW') {
        throw 'Mounted owned disk identity, size or empty state mismatch.'
    }
    $diskNumber = $ownedDisk.Number
    $diskIdentity = $ownedDisk.UniqueId
    $receipt.diskNumber = $diskNumber
    $receipt.diskIdentity = $diskIdentity
    $ownedDisk | Initialize-Disk -PartitionStyle MBR
    $partition = New-Partition -DiskNumber $diskNumber -UseMaximumSize -AssignDriveLetter
    if ($partition.DiskNumber -ne $diskNumber -or -not $partition.DriveLetter) { throw 'Owned partition identity mismatch.' }
    $partition | Format-Volume -FileSystem NTFS -NewFileSystemLabel ('TIA_' + $token.Substring(0,20)) -AllocationUnitSize 4096 -Confirm:$false | Out-Null
    $volumeRoot = [string]$partition.DriveLetter + ':\'
    @{token=$token;sizeBytes=67108864;vhdPath=$diskPath} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $volumeRoot 'runtime-pressure-owner.json') -Encoding utf8
    $receipt.volumeRoot = $volumeRoot
    $testFinished = $false
    & dotnet $assembly --disk-full $volumeRoot $token (Join-Path $output 'evidence') 2>&1 | Tee-Object -FilePath (Join-Path $output 'checks.log')
    $testFinished = $true
    if ($LASTEXITCODE -ne 0) { throw "Disk-full test process failed with exit code $LASTEXITCODE." }
    $checks = Get-Content -LiteralPath (Join-Path $output 'evidence/disk-full-checks.json') -Raw | ConvertFrom-Json
    if (-not $checks.actualDiskFull) { throw 'Test did not prove actual filesystem exhaustion.' }
    $receipt.actualDiskFull = $true
    $receipt.status = 'passed'
} catch {
    $failure = $_
    $receipt.status = if ($mounted) { 'failed' } else { 'prerequisiteBlocked' }
    $receipt.errorType = $_.Exception.GetType().FullName
    $receipt.errorId = $_.FullyQualifiedErrorId
    $receipt.errorMessage = $_.Exception.Message
} finally {
    if ($mounted -and $testFinished) {
        $current = Get-VHD -Path $diskPath
        if ($current.Path -ne $diskPath -or $current.Size -ne 67108864 -or -not $current.Attached) { throw 'Refusing dismount: owned VHD identity changed.' }
        if ($null -ne $diskNumber -and $current.DiskNumber -ne $diskNumber) { throw 'Refusing dismount: disk number changed.' }
        if ($null -ne $diskIdentity -and (Get-Disk -Number $current.DiskNumber).UniqueId -ne $diskIdentity) { throw 'Refusing dismount: disk identity changed.' }
        Dismount-VHD -Path $diskPath
        $receipt.detached = -not (Get-VHD -Path $diskPath).Attached
    } elseif ($mounted) {
        $receipt.cleanupBlocked = 'Test process completion was not confirmed; owned VHD was left attached.'
    }
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'run.json') -Encoding utf8
}
if ($failure) { Write-Output ($receipt | ConvertTo-Json -Depth 8); exit 3 }
Write-Output ($receipt | ConvertTo-Json -Depth 8)
