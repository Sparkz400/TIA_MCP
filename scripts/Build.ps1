param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:MSBuildEnableWorkloadResolver = 'false'
Push-Location $repoRoot
try {
    dotnet build TiaMcp.slnx -c $Configuration -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
    dotnet "tests/TiaMcp.Tests/bin/$Configuration/net10.0/TiaMcp.Tests.dll" --verify-worker "src/TiaMcp.Openness.V21/bin/$Configuration/net48/TiaMcp.Openness.V21.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Worker lifecycle regression check failed.' }
} finally { Pop-Location }
