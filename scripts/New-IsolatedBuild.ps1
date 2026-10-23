param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$Offline)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$bundleKind = if ($Offline) { 'offline' } else { 'engineering' }
$buildRoot = Join-Path $repoRoot ('artifacts/deployment-build/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
# Copy source into a unique tree: running clients and other chats may hold the
# normal bin/obj outputs open. Record exact input bytes, never copy those outputs.
$inputs = @(Get-ChildItem -LiteralPath $repoRoot -File | Where-Object { $_.Name -match '\.(slnx|props|targets)$|^NuGet\.Config$|^global\.json$|^package(-lock)?\.json$|^playwright\.config\.[cm]?js$' })
foreach ($folder in @('src','tests','contracts','scripts','fixtures','docs','third-party','ui','e2e','.github')) {
    $inputs += @(Get-ChildItem -LiteralPath (Join-Path $repoRoot $folder) -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|__pycache__|node_modules|test-results|playwright-report)[\\/]' })
}
foreach ($name in @('README.md','LICENSE','NOTICE','CONTRIBUTING.md','SECURITY.md')) {
    $inputs += Get-Item -LiteralPath (Join-Path $repoRoot $name)
}
$records = @()
foreach ($file in $inputs) {
    $relative = $file.FullName.Substring($repoRoot.Length + 1)
    $before = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $target = Join-Path $buildRoot $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
    $copied = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if ($before -ne $copied) { throw "Source changed during snapshot: $relative. Retry a new isolated build." }
    $records += @{path=$relative;sha256=$copied.ToLowerInvariant()}
}
foreach ($record in $records) {
    if ((Get-FileHash -LiteralPath (Join-Path $repoRoot $record.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $record.sha256) {
        throw "Source changed during snapshot: $($record.path). Retry a new isolated build."
    }
}
@{schemaVersion=1;createdAtUtc=[DateTime]::UtcNow.ToString('O');configuration=$Configuration;bundleKind=$bundleKind;files=$records} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $buildRoot 'build-inputs.json') -Encoding UTF8
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$packages = Join-Path $repoRoot '.packages'
Push-Location $buildRoot
try {
    $buildTarget = if ($Offline) { 'src/TiaMcp.Host/TiaMcp.Host.csproj' } else { 'TiaMcp.slnx' }
    $offlineProperty = '-p:OfflinePackage=' + $Offline.ToString().ToLowerInvariant()
    dotnet build $buildTarget -c $Configuration -m:1 -nr:false $offlineProperty "-p:RestorePackagesPath=$packages" --ignore-failed-sources | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Isolated build failed.' }
    if (-not $Offline) {
        dotnet "tests/TiaMcp.Tests/bin/$Configuration/net10.0/TiaMcp.Tests.dll" --verify-worker "src/TiaMcp.Openness.V21/bin/$Configuration/net48/TiaMcp.Openness.V21.exe" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Worker lifecycle guard failed.' }
    }
    dotnet publish src/TiaMcp.Host/TiaMcp.Host.csproj -c $Configuration --no-restore --self-contained false -o published-host -m:1 -nr:false $offlineProperty "-p:RestorePackagesPath=$packages" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Isolated publish failed.' }
    $outputs = @(Get-ChildItem -LiteralPath (Join-Path $buildRoot 'published-host') -Recurse -File)
    foreach ($worker in $(if ($Offline) { @() } else { @('TiaMcp.Openness.V21','TiaMcp.Simulator.V8') })) {
        $outputs += @(Get-ChildItem -LiteralPath (Join-Path $buildRoot "src/$worker/bin/$Configuration/net48") -File |
            Where-Object { $_.Extension -in '.exe','.dll','.config','.json' })
    }
    @{schemaVersion=1;configuration=$Configuration;bundleKind=$bundleKind;inputManifestSha256=(Get-FileHash -LiteralPath (Join-Path $buildRoot 'build-inputs.json')).Hash.ToLowerInvariant();
        files=@($outputs | ForEach-Object { @{path=$_.FullName.Substring($buildRoot.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()} })} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $buildRoot 'build-outputs.json') -Encoding UTF8
} finally { Pop-Location }
Write-Output $buildRoot
