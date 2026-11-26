<#
.SYNOPSIS
Measures line coverage of the managed offline suites and writes a Cobertura report with per-assembly and per-source-project summaries.

.DESCRIPTION
Runs scripts/Test-Offline.ps1 -Suite Managed (the same suite selection, sharding and post-suite checks) under
`dotnet-coverage collect`, whose profiler is inherited by the .NET processes the runner starts. TiaMcp assemblies are
measured, test assemblies included, because several suites compile worker and Host sources (src/TiaMcp.Openness.V21/*.cs
and others) into the test assembly itself; the test code under tests/ is left out, as is third-party code. Host
processes started by the Python checks inherit the profiler too, but a Host that is killed rather than exited may not
flush its counts. The runner skips those checks when any suite fails, and no local run up to 26 November 2026 reached
them, so Host figures come from the suites alone.

The tool is installed into artifacts/tools/dotnet-coverage at the pinned version below (no global install). Results go to
<OutputDirectory>/coverage.cobertura.xml and <OutputDirectory>/coverage-summary.json; the runner's own output goes to
<OutputDirectory>/offline-validation. The report measures; it gates nothing.

The summary has two views. `assemblies` lists product assemblies as loaded (test assemblies left out, generated
sources such as RegexGenerator.g.cs included). `sources` groups by source project, src/<project>: each line of a file
under src/ counts once, covered if any assembly that compiles it ran it, so worker code linked into test assemblies is
attributed to its source file. Only files compiled into an assembly that a selected suite loaded are counted.

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
# TiaMcp assemblies, test assemblies included: they compile linked src/ files. ModulePaths and Sources are regular
# expressions over the full module or source path; the test code itself (tests/, and generated files of test projects)
# is left out by source path.
$settings = Join-Path $output 'coverage.settings.xml'
@'
<?xml version="1.0" encoding="utf-8"?>
<Configuration>
  <CodeCoverage>
    <ModulePaths>
      <Include>
        <ModulePath>.*[\\/]TiaMcp\.[^\\/]*\.(dll|exe)$</ModulePath>
      </Include>
    </ModulePaths>
    <Sources>
      <Exclude>
        <Source>{0}[\\/]tests[\\/].*</Source>
        <Source>.*[\\/]obj[\\/][^\\/]*\.Tests[\\/].*</Source>
      </Exclude>
    </Sources>
    <IncludeTestAssembly>true</IncludeTestAssembly>
  </CodeCoverage>
</Configuration>
'@ -f [Security.SecurityElement]::Escape([regex]::Escape($repoRoot)) | Set-Content -LiteralPath $settings -Encoding UTF8

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
# Lines of files under src/ are also counted once per source project, whichever assemblies compile the file. Matching the
# last src/TiaMcp.* segment keeps this independent of the checkout path (a merge may read reports from other machines).
$lines = @{}
$sourceLines = @{}
$sourcePattern = [regex]'^.*[\\/]src[\\/](TiaMcp\.[^\\/]+)[\\/](.+)$'
$reader = [Xml.XmlReader]::Create($report, [Xml.XmlReaderSettings]@{DtdProcessing = 'Prohibit'; XmlResolver = $null})
try {
    $package = $null; $file = $null
    while ($reader.Read()) {
        if ($reader.NodeType -ne [Xml.XmlNodeType]::Element) { continue }
        switch ($reader.LocalName) {
            'package' { $package = $reader.GetAttribute('name'); if (-not $lines.ContainsKey($package)) { $lines[$package] = @{} } }
            'class' {
                $file = $reader.GetAttribute('filename')
                $match = $sourcePattern.Match([string]$file)
                $project = $null; $relative = $null
                if ($match.Success) {
                    $project = 'src/' + $match.Groups[1].Value
                    $relative = ($project + '/' + ($match.Groups[2].Value -replace '\\', '/')).ToLowerInvariant()
                    if (-not $sourceLines.ContainsKey($project)) { $sourceLines[$project] = @{} }
                }
            }
            'line' {
                if ($package) {
                    $number = $reader.GetAttribute('number')
                    $key = $file + ':' + $number
                    $hit = [long]$reader.GetAttribute('hits') -gt 0
                    $lines[$package][$key] = $hit -or [bool]$lines[$package][$key]
                    if ($project) {
                        $sourceKey = $relative + ':' + $number
                        $sourceLines[$project][$sourceKey] = $hit -or [bool]$sourceLines[$project][$sourceKey]
                    }
                }
            }
        }
    }
} finally { $reader.Dispose() }

function Get-LineSummary($set) {
    $valid = if ($set) { $set.Count } else { 0 }
    $covered = if ($set) { @($set.Values | Where-Object { $_ }).Count } else { 0 }
    [ordered]@{
        linesValid = $valid
        linesCovered = $covered
        lineCoveragePercent = $(if ($valid) { [math]::Round(100.0 * $covered / $valid, 1) } else { $null })
    }
}
$assemblies = [ordered]@{}
foreach ($name in @($summaryAssemblies + @($lines.Keys | Where-Object { $_ -notlike '*.Tests' } | Sort-Object)) | Select-Object -Unique) {
    $assemblies[$name] = Get-LineSummary $lines[$name]
}
$sources = [ordered]@{}
foreach ($name in @(@($summaryAssemblies + 'TiaMcp.Openness.V21' | ForEach-Object { "src/$_" }) + @($sourceLines.Keys | Sort-Object)) | Select-Object -Unique) {
    $sources[$name] = Get-LineSummary $sourceLines[$name]
}
$summary = [ordered]@{
    schemaVersion = 2
    tool = "dotnet-coverage $toolVersion"
    configuration = $Configuration
    shard = $(if ($MergeReports) { $null } else { [ordered]@{index = $ShardIndex; count = $ShardCount} })
    mergedReports = $(if ($MergeReports) { $MergeReports.Count } else { $null })
    suites = $(if ($MergeReports) { @() } else { try { @((Get-Content -LiteralPath (Join-Path $validation 'result.json') -Raw | ConvertFrom-Json).managedShard.suites) } catch { @() } })
    testExitCode = $testExit
    report = 'coverage.cobertura.xml'
    assemblies = $assemblies
    sources = $sources
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'coverage-summary.json') -Encoding UTF8
$assemblies.GetEnumerator() | ForEach-Object {
    [pscustomobject]@{Assembly = $_.Key; Lines = $_.Value.linesValid; Covered = $_.Value.linesCovered; Percent = $_.Value.lineCoveragePercent}
} | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
$sources.GetEnumerator() | ForEach-Object {
    [pscustomobject]@{Source = $_.Key; Lines = $_.Value.linesValid; Covered = $_.Value.linesCovered; Percent = $_.Value.lineCoveragePercent}
} | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host "Coverage report: $report"
if ($testExit -ne 0) {
    Write-Warning "The offline runner failed (exit $testExit); coverage covers the suites that ran."
    if ($FailOnTestFailure) { throw 'Managed offline suites failed.' }
}
$output
