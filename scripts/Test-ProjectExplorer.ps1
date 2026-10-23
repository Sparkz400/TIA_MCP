param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [switch]$Browser,
    [string]$Snapshot,
    [string]$HostExecutable,
    [string]$WorkerExecutable
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet'
Push-Location $repoRoot
try {
    if (-not $SkipBuild) {
        dotnet build tests/TiaMcp.ProjectModel.Tests/TiaMcp.ProjectModel.Tests.csproj -c $Configuration -m:1 -nr:false --ignore-failed-sources
        if ($LASTEXITCODE -ne 0) { throw 'Explorer model test build failed.' }
    }
    dotnet "tests/TiaMcp.ProjectModel.Tests/bin/$Configuration/net10.0/TiaMcp.ProjectModel.Tests.dll"
    if ($LASTEXITCODE -ne 0) { throw 'Explorer model/artifact checks failed.' }
    python -B tests/ProjectModelContracts.py
    if ($LASTEXITCODE -ne 0) { throw 'Explorer JSON Schema checks failed; jsonschema>=4.18 is a test-only prerequisite.' }
    node --test ui/project-explorer/tests/model.test.cjs ui/project-explorer/tests/client.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Explorer client/model checks failed.' }
    if ($HostExecutable) {
        python -B scripts/test_project_explorer_host.py --host $HostExecutable
        if ($LASTEXITCODE -ne 0) { throw 'Explorer MCP negotiation checks failed.' }
    }
    if ($WorkerExecutable) {
        powershell.exe -NoLogo -NoProfile -NonInteractive -File (Join-Path $repoRoot 'tests/ProjectModelNativeGuards.ps1') -WorkerPath $WorkerExecutable
        if ($LASTEXITCODE -ne 0) { throw 'Explorer native traversal guard checks failed.' }
    }
    if ($Browser) {
        $browserArgs = @('ui/project-explorer/tests/browser.mjs')
        if ($Snapshot) { $browserArgs += @('--snapshot', $Snapshot) }
        & node @browserArgs
        if ($LASTEXITCODE -ne 0) { throw 'Explorer browser checks failed.' }
    }
} finally { Pop-Location }
