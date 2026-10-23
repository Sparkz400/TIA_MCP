param(
    [Parameter(Mandatory=$true)][string]$TranslationWorkbook,
    [Parameter(Mandatory=$true)][string]$WatchReorderRun,
    [Parameter(Mandatory=$true)][string]$WatchDeleteRun,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputDirectory)
foreach ($inputPath in @($TranslationWorkbook,(Join-Path $WatchReorderRun 'review.json'),(Join-Path $WatchDeleteRun 'review.json'))) {
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Retained fixture prerequisite missing: $inputPath" }
}
if (Test-Path -LiteralPath $output) { throw 'Select a fresh evidence output directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
$result=@{status='running';nativeStarted=$false;nativeQualification=$false;inputs=@()}
foreach ($inputPath in @($TranslationWorkbook,(Join-Path $WatchReorderRun 'review.json'),(Join-Path $WatchDeleteRun 'review.json'))) {
    $result.inputs += @{path=[IO.Path]::GetFullPath($inputPath);sha256=(Get-FileHash -LiteralPath $inputPath).Hash.ToLowerInvariant()}
}
Push-Location $repoRoot
try {
    Start-Transcript -Path (Join-Path $output 'validation.log') | Out-Null
    dotnet run --project tests/TiaMcp.TranslationCategoryProbe.Tests/TiaMcp.TranslationCategoryProbe.Tests.csproj -c $Configuration --artifacts-path (Join-Path $output 'dotnet') -- $TranslationWorkbook (Join-Path $output 'translation')
    if ($LASTEXITCODE -ne 0) { throw 'Retained translation category fixture failed.' }
    dotnet run --project tests/TiaMcp.WatchReindex.Tests/TiaMcp.WatchReindex.Tests.csproj -c $Configuration --artifacts-path (Join-Path $output 'dotnet') -- (Join-Path $output 'watch') $WatchReorderRun $WatchDeleteRun
    if ($LASTEXITCODE -ne 0) { throw 'Retained watch reindex fixture failed.' }
    foreach ($inputRecord in $result.inputs) {
        if ((Get-FileHash -LiteralPath $inputRecord.path).Hash.ToLowerInvariant() -ne $inputRecord.sha256) { throw 'Retained fixture changed.' }
    }
    $result.status='passed'
} catch { $result.status='failed'; $result.error=$_.Exception.Message; throw }
finally {
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
    Stop-Transcript -ErrorAction SilentlyContinue | Out-Null
    Pop-Location
}
