param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ('artifacts/workstation-boundaries/' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Fresh output is required; prior evidence is immutable.' }
New-Item -ItemType Directory -Path $output | Out-Null
$build = Join-Path $output 'dotnet'
$result = @{status='running';nativeStarted=$false;checks=@();startedAtUtc=[DateTime]::UtcNow.ToString('O')}
Push-Location $repoRoot
try {
    Start-Transcript -Path (Join-Path $output 'validation.log') | Out-Null
    # These assemblies require installed Siemens reference assemblies at build
    # time. Their tests exercise boundaries without creating a Portal session.
    foreach ($suite in @('TiaMcp.WatchAdapter.Boundary.Tests','TiaMcp.ComponentQualification.Boundary.Tests','TiaMcp.TestSuite.Worker.Tests')) {
        dotnet build "tests/$suite/$suite.csproj" -c $Configuration --artifacts-path $build -m:1 -nr:false
        if ($LASTEXITCODE -ne 0) { throw "Workstation build failed: $suite" }
        & (Join-Path $build "bin/$suite/$($Configuration.ToLowerInvariant())/$suite.exe")
        if ($LASTEXITCODE -ne 0) { throw "Worker boundary suite failed: $suite" }
        $result.checks += $suite
    }
    dotnet run --project tests/TiaMcp.Tests/TiaMcp.Tests.csproj -c $Configuration --artifacts-path $build -- --verify-worker (Join-Path $build "bin/TiaMcp.Openness.V21/$($Configuration.ToLowerInvariant())/TiaMcp.Openness.V21.exe")
    if ($LASTEXITCODE -ne 0) { throw 'Compiled worker lifetime guard failed.' }
    $result.checks += 'compiled-worker-lifetime-guard'
    $result.status = 'passed'
} catch { $result.status='failed'; $result.error=$_.Exception.Message; throw }
finally {
    $result.completedAtUtc=[DateTime]::UtcNow.ToString('O')
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
    Stop-Transcript -ErrorAction SilentlyContinue | Out-Null
    Pop-Location
}
