param([switch]$SkipIndexBuild, [string]$BundlePath, [switch]$RunSessionRelayTests)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Test-EnvironmentDiagnostics.ps1')
& (Join-Path $PSScriptRoot 'Test-Deployment.ps1')
& (Join-Path $PSScriptRoot 'Test-GuestAccessStartup.ps1')
if ($BundlePath) { & (Join-Path $PSScriptRoot 'Test-TiaOpennessRegistration.ps1') -BundlePath $BundlePath }
else { Write-Host 'Registration inspection not run: supply -BundlePath with a verified release.' }
if ($RunSessionRelayTests) {
    if (-not $BundlePath) { throw '-RunSessionRelayTests requires a current -BundlePath and a logged-in Windows session.' }
    python -B (Join-Path $PSScriptRoot 'test_session_relay.py') --bundle $BundlePath
    if ($LASTEXITCODE -ne 0) { throw 'Session relay checks failed.' }
}
foreach ($name in @('test_qualify_deployment.py','test_reliability.py','test_project_index_qualifier.py','test_guest_qualification.py','test_ssh_recovery.py')) {
    python -B (Join-Path $PSScriptRoot $name)
    if ($LASTEXITCODE -ne 0) { throw "Deployment/reliability check failed: $name" }
}
if (-not $SkipIndexBuild) {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.Deployment.Tests/TiaMcp.Deployment.Tests.csproj') -c Release -- (Join-Path $repoRoot 'artifacts/project-index-store-tests.json')
    if ($LASTEXITCODE -ne 0) { throw 'Index store tests failed.' }
}
