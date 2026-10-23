param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$BuildRoot = (Split-Path $PSScriptRoot -Parent),
    [switch]$Mcp
)
$ErrorActionPreference = 'Stop'
$BuildRoot = (Resolve-Path -LiteralPath $BuildRoot).Path
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
try {
    $testTemp = Join-Path $BuildRoot 'artifacts/library-test-temp'
    New-Item -ItemType Directory -Path $testTemp -Force | Out-Null
    $env:TEMP = $testTemp
    $env:TMP = $testTemp
    foreach ($project in @('TiaMcp.Library.Tests', 'TiaMcp.LibraryHmi.Tests', 'TiaMcp.PlcLibraryIntegration.Tests')) {
        $runner = Join-Path $BuildRoot "tests/$project/bin/$Configuration/net10.0/$project.dll"
        if (-not (Test-Path -LiteralPath $runner)) { throw "Build the solution before testing: $runner" }
        dotnet $runner
        if ($LASTEXITCODE -ne 0) { throw "Library regression suite failed: $project" }
    }
    python -B (Join-Path $PSScriptRoot 'test_library_api_probe.py')
    if ($LASTEXITCODE -ne 0) { throw 'Library API probe regression suite failed.' }
    python -B (Join-Path $PSScriptRoot 'test_library_hmi_evidence.py')
    if ($LASTEXITCODE -ne 0) { throw 'Library HMI evidence regression suite failed.' }
    python -B (Join-Path $PSScriptRoot 'test_plc_library_integration_plan.py')
    if ($LASTEXITCODE -ne 0) { throw 'PLC integration plan qualification harness checks failed.' }
    $componentBoundary = Join-Path $BuildRoot "tests/TiaMcp.ComponentQualification.Boundary.Tests/bin/$Configuration/net48/TiaMcp.ComponentQualification.Boundary.Tests.exe"
    if (-not (Test-Path -LiteralPath $componentBoundary)) { throw "Build the solution before testing: $componentBoundary" }
    & $componentBoundary
    if ($LASTEXITCODE -ne 0) { throw 'Component qualification dispatcher boundary checks failed.' }
    python -B (Join-Path $PSScriptRoot 'test_plc_component.py')
    if ($LASTEXITCODE -ne 0) { throw 'Component qualification harness checks failed.' }
    if ($Mcp) {
        $hostExecutable = Join-Path $BuildRoot "src/TiaMcp.Host/bin/$Configuration/net10.0-windows/TiaMcp.Host.exe"
        python -B (Join-Path $PSScriptRoot 'qualify_library_intake.py') --host $hostExecutable --artifacts (Join-Path $BuildRoot 'artifacts/library-mcp-tests')
        if ($LASTEXITCODE -ne 0) { throw 'Library package MCP qualification failed.' }
    }
} finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
}
