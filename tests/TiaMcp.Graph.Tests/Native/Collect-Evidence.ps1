param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9-]*$')][string]$Profile,
    [Parameter(Mandatory)][ValidateSet('completed', 'empty-condition-rejected', 'duplicate-number-rejected')][string]$Outcome
)
$ErrorActionPreference = 'Stop'
$runPath = [IO.Path]::GetFullPath($RunDirectory)
$nativeBin = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'bin'))
if (-not $runPath.StartsWith($nativeBin.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must originate inside this native runner bin directory.' }
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../fixtures/native-v21'))
$destination = Join-Path $fixtureRoot $Profile
if (Test-Path -LiteralPath $destination) { throw 'Profile already exists; evidence is not overwritten.' }
if ($Outcome -eq 'completed') {
    $receipt = Get-Content -LiteralPath (Join-Path $runPath 'result.json') -Raw | ConvertFrom-Json
    if ($receipt.completed -ne $true) { throw 'Native run did not complete.' }
} else {
    $failure = Get-Content -LiteralPath (Join-Path $runPath 'failure.json') -Raw | ConvertFrom-Json
    if ($Outcome -eq 'empty-condition-rejected' -and ($failure.stage -ne 'no-op-import' -or $failure.message -notmatch 'Supervision')) { throw 'Unexpected empty-condition rejection.' }
    if ($Outcome -eq 'duplicate-number-rejected' -and ($failure.stage -ne 'import-authored-seed' -or $failure.message -notmatch 'transition number 1 exists twice')) { throw 'Unexpected duplicate-number rejection.' }
}
$null = New-Item -ItemType Directory -Path $destination
$names = @('seed.xml', 'baseline.xml', 'roundtrip.xml', 'reopened.xml', 'recovered.xml', 'intent.json', 'baseline.json',
    'project-ownership.json', 'process-create.json', 'process-reopen.json', 'process-recovery.json', 'recovered-project.json',
    'compile-imported.json', 'compile-roundtrip.json', 'compile-reopened.json', 'compile-recovered.json', 'result.json', 'failure.json')
$artifacts = @()
foreach ($name in $names) {
    $source = Join-Path $runPath $name
    if (Test-Path -LiteralPath $source) {
        $target = Join-Path $destination $name
        Copy-Item -LiteralPath $source -Destination $target
        $artifacts += [ordered]@{ file = $name; sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
}
$manifestPath = Join-Path $fixtureRoot 'manifest.json'
$manifest = if (Test-Path -LiteralPath $manifestPath) { Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable } else { [ordered]@{ format = 'tia-graph-native-fixtures.v1'; profiles = @() } }
$manifest.profiles += [ordered]@{ directory = $Profile; outcome = $Outcome; sourceRun = $runPath; artifacts = $artifacts }
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Write-Output "Collected $Profile with $($artifacts.Count) original artifacts."
