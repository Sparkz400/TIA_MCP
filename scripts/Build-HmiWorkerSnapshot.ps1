param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$snapshot = Join-Path $repository ('artifacts/hmi-worker-build/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $snapshot -Force | Out-Null
$sources = @()
# The worker now links Core and native qualification sources and references the
# Test Suite project. Freeze their source trees too; never reuse bin/obj outputs.
foreach ($folder in @('src', 'scripts/native', 'fixtures')) {
    $sources += @(Get-ChildItem -LiteralPath (Join-Path $repository $folder) -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|__pycache__)[\\/]' })
}
foreach ($name in @('Directory.Build.props','NuGet.Config','global.json')) { $sources += Get-Item -LiteralPath (Join-Path $repository $name) }
$records = @()
foreach ($file in $sources) {
    $relative = $file.FullName.Substring($repository.Length + 1)
    $before = (Get-FileHash -LiteralPath $file.FullName).Hash
    $target = Join-Path $snapshot $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
    $copied = (Get-FileHash -LiteralPath $target).Hash
    if ($before -ne $copied) { throw "Source changed while copied: $relative" }
    $records += @{path=$relative;sha256=$copied.ToLowerInvariant()}
}
foreach ($record in $records) {
    if ((Get-FileHash -LiteralPath (Join-Path $repository $record.path)).Hash.ToLowerInvariant() -ne $record.sha256) { throw "Worker source changed during snapshot: $($record.path)" }
}
@{schemaVersion=1;purpose='Native HMI worker qualification only; full release packaging uses New-IsolatedBuild.ps1';createdAtUtc=[DateTime]::UtcNow.ToString('O');files=$records} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $snapshot 'input-manifest.json') -Encoding utf8
$env:DOTNET_CLI_HOME = Join-Path $repository '.dotnet'
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet build (Join-Path $snapshot 'src/TiaMcp.Openness.V21/TiaMcp.Openness.V21.csproj') -c $Configuration -m:1 -nr:false -p:NuGetAudit=false "-p:RestorePackagesPath=$(Join-Path $repository '.packages')" --ignore-failed-sources -v:q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Isolated HMI qualification worker build failed.' }
$worker = Join-Path $snapshot "src/TiaMcp.Openness.V21/bin/$Configuration/net48/TiaMcp.Openness.V21.exe"
@{schemaVersion=1;inputManifestSha256=(Get-FileHash -LiteralPath (Join-Path $snapshot 'input-manifest.json')).Hash.ToLowerInvariant();worker=$worker;workerSha256=(Get-FileHash -LiteralPath $worker).Hash.ToLowerInvariant()} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $snapshot 'output-manifest.json') -Encoding utf8
Write-Output $worker
