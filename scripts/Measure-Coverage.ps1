<#
.SYNOPSIS
Measures line coverage of the managed offline suites and writes a Cobertura report with a per-assembly summary.

.DESCRIPTION
Runs scripts/Test-Offline.ps1 -Suite Managed (the same suite selection, sharding and post-suite checks) under
`dotnet-coverage collect`, whose profiler is inherited by the .NET processes the runner starts. Only TiaMcp product
assemblies are measured; test assemblies and third-party code are left out. Host processes started by the Python
checks inherit the profiler too, but a Host that is killed rather than exited may not flush its counts; local runs on
25 November 2026 covered only shards without those checks, so Host figures are unverified.

The tool is installed into artifacts/tools/dotnet-coverage at the pinned version below (no global install). Results go to
<OutputDirectory>/coverage.cobertura.xml and <OutputDirectory>/coverage-summary.json; the runner's own output goes to
<OutputDirectory>/offline-validation. The report measures; it gates nothing.

Limit: several suites compile worker sources (src/TiaMcp.Openness.V21/*.cs and others) into the test assembly itself.
Test assemblies are excluded, so that linked worker code is not measured; only code in product assemblies is.

.EXAMPLE
./scripts/Measure-Coverage.ps1                              # every managed suite
./scripts/Measure-Coverage.ps1 -ShardCount 16 -ShardIndex 15  # a few suites, for a quick local check
#>
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [ValidateRange(1, 16)][int]$ShardCount = 1,
    [ValidateRange(0, 15)][int]$ShardIndex = 0,
    # A failing suite still yields a report; pass this to fail the script as well.
    [switch]$FailOnTestFailure,
    # Runs nothing: merges these Cobertura reports (for example one per CI shard) and summarizes the result.
    [string[]]$MergeReports
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$toolVersion = '18.12.0' # dotnet-coverage (Microsoft.CodeCoverage); exact, so a new release cannot change the report.
# Assemblies the summary lists even when no suite of the selected shard loads them.
$summaryAssemblies = @('TiaMcp.Core', 'TiaMcp.Host', 'TiaMcp.OpcUa', 'TiaMcp.OpcUa.Runtime', 'TiaMcp.Graph', 'TiaMcp.Translations')

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ('artifacts/coverage/' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }))
if (Test-Path -LiteralPath $output) { throw 'Select a fresh output directory.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null

$toolPath = Join-Path $repoRoot "artifacts/tools/dotnet-coverage/$toolVersion"
$tool = Join-Path $toolPath $(if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'dotnet-coverage.exe' } else { 'dotnet-coverage' })
if (-not (Test-Path -LiteralPath $tool)) {
    dotnet tool install dotnet-coverage --version $toolVersion --tool-path $toolPath
    if ($LASTEXITCODE -ne 0) { throw "Installing dotnet-coverage $toolVersion failed." }
}
$report = Join-Path $output 'coverage.cobertura.xml'
$validation = Join-Path $output 'offline-validation'

if ($MergeReports) {
    $inputs = @($MergeReports | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
    & $tool merge --output $report --output-format cobertura @inputs
    if ($LASTEXITCODE -ne 0) { throw 'Merging the coverage reports failed.' }
    $testExit = 0
} else {
# Product assemblies only. ModulePaths are regular expressions over the full module path.
$settings = Join-Path $output 'coverage.settings.xml'
@'
<?xml version="1.0" encoding="utf-8"?>
<Configuration>
  <CodeCoverage>
    <ModulePaths>
      <Include>
        <ModulePath>.*[\\/]TiaMcp\.[^\\/]*\.(dll|exe)$</ModulePath>
      </Include>
      <Exclude>
        <ModulePath>.*\.Tests\.(dll|exe)$</ModulePath>
      </Exclude>
    </ModulePaths>
    <IncludeTestAssembly>false</IncludeTestAssembly>
  </CodeCoverage>
</Configuration>
'@ | Set-Content -LiteralPath $settings -Encoding UTF8

$runner = Join-Path $PSScriptRoot 'Test-Offline.ps1'
$pwsh = (Get-Process -Id $PID).Path
$previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
# A build node that outlives the run would keep the collector waiting for its profiled children.
$env:MSBUILDDISABLENODEREUSE = '1'
try {
    & $tool collect --settings $settings --output $report --output-format cobertura -- `
        $pwsh -NoProfile -File $runner -Suite Managed -Configuration $Configuration -OutputDirectory $validation -ShardCount $ShardCount -ShardIndex $ShardIndex
    $testExit = $LASTEXITCODE
} finally { $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse }
}
if (-not (Test-Path -LiteralPath $report)) { throw "dotnet-coverage wrote no report (exit $testExit)." }

# Cobertura packages are assemblies; count each source line once per assembly (a line can appear in several methods).
$lines = @{}
$reader = [Xml.XmlReader]::Create($report, [Xml.XmlReaderSettings]@{DtdProcessing = 'Prohibit'; XmlResolver = $null})
try {
    $package = $null; $file = $null
    while ($reader.Read()) {
        if ($reader.NodeType -ne [Xml.XmlNodeType]::Element) { continue }
        switch ($reader.LocalName) {
            'package' { $package = $reader.GetAttribute('name'); if (-not $lines.ContainsKey($package)) { $lines[$package] = @{} } }
            'class' { $file = $reader.GetAttribute('filename') }
            'line' {
                if ($package) {
                    $key = $file + ':' + $reader.GetAttribute('number')
                    $hit = [long]$reader.GetAttribute('hits') -gt 0
                    $lines[$package][$key] = $hit -or [bool]$lines[$package][$key]
                }
            }
        }
    }
} finally { $reader.Dispose() }

$assemblies = [ordered]@{}
foreach ($name in @($summaryAssemblies + @($lines.Keys | Sort-Object)) | Select-Object -Unique) {
    $set = $lines[$name]
    $valid = if ($set) { $set.Count } else { 0 }
    $covered = if ($set) { @($set.Values | Where-Object { $_ }).Count } else { 0 }
    $assemblies[$name] = [ordered]@{
        linesValid = $valid
        linesCovered = $covered
        lineCoveragePercent = $(if ($valid) { [math]::Round(100.0 * $covered / $valid, 1) } else { $null })
    }
}
$summary = [ordered]@{
    schemaVersion = 1
    tool = "dotnet-coverage $toolVersion"
    configuration = $Configuration
    shard = $(if ($MergeReports) { $null } else { [ordered]@{index = $ShardIndex; count = $ShardCount} })
    mergedReports = $(if ($MergeReports) { $MergeReports.Count } else { $null })
    suites = $(if ($MergeReports) { @() } else { try { @((Get-Content -LiteralPath (Join-Path $validation 'result.json') -Raw | ConvertFrom-Json).managedShard.suites) } catch { @() } })
    testExitCode = $testExit
    report = 'coverage.cobertura.xml'
    assemblies = $assemblies
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'coverage-summary.json') -Encoding UTF8
$assemblies.GetEnumerator() | ForEach-Object {
    [pscustomobject]@{Assembly = $_.Key; Lines = $_.Value.linesValid; Covered = $_.Value.linesCovered; Percent = $_.Value.lineCoveragePercent}
} | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host "Coverage report: $report"
if ($testExit -ne 0) {
    Write-Warning "The offline runner failed (exit $testExit); coverage covers the suites that ran."
    if ($FailOnTestFailure) { throw 'Managed offline suites failed.' }
}
$output
