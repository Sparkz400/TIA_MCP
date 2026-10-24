param([switch]$SkipBuild, [ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [string]$TestTempDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration $Configuration }
Push-Location $repoRoot
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
try {
    # Keep test scratch paths short and writable, including restricted build agents.
    $testTemp = if ($TestTempDirectory) { [IO.Path]::GetFullPath($TestTempDirectory) } else { Join-Path $repoRoot 'artifacts/test-temp' }
    New-Item -ItemType Directory -Path $testTemp -Force | Out-Null
    $env:TEMP = $testTemp
    $env:TMP = $testTemp
    & (Join-Path $PSScriptRoot 'Test-Deployment.ps1')
    python (Join-Path $PSScriptRoot 'test_contracts.py')
    if ($LASTEXITCODE -ne 0) { throw 'Feature contract checks failed.' }
    dotnet "tests/TiaMcp.Inventory.Tests/bin/$Configuration/net10.0-windows/TiaMcp.Inventory.Tests.dll"
    if ($LASTEXITCODE -ne 0) { throw 'Device inventory checks failed.' }
    dotnet "tests/TiaMcp.PhysicalRuntime.Tests/bin/$Configuration/net10.0/TiaMcp.PhysicalRuntime.Tests.dll" (Join-Path $testTemp 'physical-runtime-results.json')
    if ($LASTEXITCODE -ne 0) { throw 'Physical runtime protocol contract checks failed.' }
    python -B scripts/test_physical_runtime_contracts.py --fixture (Join-Path $testTemp 'physical-runtime-results.json')
    if ($LASTEXITCODE -ne 0) { throw 'Physical runtime schema/value cross-check failed.' }
    python -B scripts/runtime_pressure_checks.py (Join-Path $testTemp 'physical-runtime-results.json.polling/pressure')
    if ($LASTEXITCODE -ne 0) { throw 'Independent runtime pressure chain/value/schedule checks failed.' }
    dotnet "tests/TiaMcp.PhysicalRuntime.Tests/bin/$Configuration/net10.0/TiaMcp.PhysicalRuntime.Tests.dll" --tls-fixtures (Join-Path $testTemp 'runtime-tls')
    if ($LASTEXITCODE -ne 0) { throw 'Runtime TLS fixture generation failed.' }
    python -B scripts/qualify_runtime_profiles.py --assembly "tests/TiaMcp.PhysicalRuntime.Tests/bin/$Configuration/net10.0/TiaMcp.PhysicalRuntime.Tests.dll" --tls (Join-Path $testTemp 'runtime-tls') --runtime-directory "src/TiaMcp.Host/bin/$Configuration/net10.0-windows" --output (Join-Path $testTemp 'runtime-profiles')
    if ($LASTEXITCODE -ne 0) { throw 'Advertised runtime profile and refusal qualification failed.' }
    python -B scripts/test_runtime_session_host.py --host "src/TiaMcp.Host/bin/$Configuration/net10.0-windows/TiaMcp.Host.exe" --tls (Join-Path $testTemp 'runtime-tls') --output (Join-Path $testTemp 'runtime-session-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Runtime session TLS/MCP workflow failed.' }
    $uaOutput = Join-Path $testTemp ('opcua-runtime-' + [Guid]::NewGuid().ToString('N'))
    dotnet "tests/TiaMcp.OpcUa.Runtime.Tests/bin/$Configuration/net10.0/TiaMcp.OpcUa.Runtime.Tests.dll" --output (Join-Path $uaOutput 'managed')
    if ($LASTEXITCODE -ne 0) { throw 'Secure OPC UA local-server qualification failed.' }
    python -B scripts/qualify_opcua_runtime.py --host "src/TiaMcp.Host/bin/$Configuration/net10.0-windows/TiaMcp.Host.exe" --server "tests/TiaMcp.OpcUa.Runtime.Tests/bin/$Configuration/net10.0/TiaMcp.OpcUa.Runtime.Tests.dll" --output (Join-Path $uaOutput 'mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Secure OPC UA local-server/MCP qualification failed.' }
    $translationOutput = Join-Path $testTemp ('translation-' + [Guid]::NewGuid().ToString('N'))
    dotnet "tests/TiaMcp.TranslationHost.Tests/bin/$Configuration/net10.0-windows/TiaMcp.TranslationHost.Tests.dll" (Join-Path $translationOutput 'host')
    if ($LASTEXITCODE -ne 0) { throw 'Offline translation Host checks failed.' }
    dotnet "tests/TiaMcp.TranslationImport.Tests/bin/$Configuration/net10.0/TiaMcp.TranslationImport.Tests.dll" (Join-Path $translationOutput 'import')
    if ($LASTEXITCODE -ne 0) { throw 'Disabled translation import interruption checks failed.' }
    python -B (Join-Path $PSScriptRoot 'test_translation_host_mcp.py') --host "src/TiaMcp.Host/bin/$Configuration/net10.0-windows/TiaMcp.Host.exe" --output (Join-Path $translationOutput 'mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Offline translation MCP workflow failed.' }
    dotnet "tests/TiaMcp.Diagnostics.Tests/bin/$Configuration/net10.0/TiaMcp.Diagnostics.Tests.dll"
    if ($LASTEXITCODE -ne 0) { throw 'Offline trace diagnostic checks failed.' }
    python (Join-Path $PSScriptRoot 'test_conformance.py')
    if ($LASTEXITCODE -ne 0) { throw 'Conformance generator/harness tests failed.' }
    foreach ($test in @('test_safety_qualification.py', 'test_engineering_preservation.py', 'test_encoder_hardware_qualification.py')) {
        python (Join-Path $PSScriptRoot $test)
        if ($LASTEXITCODE -ne 0) { throw "Engineering evidence check failed: $test" }
    }
    foreach ($test in @('test_block_test_program.py', 'test_block_test_executor.py', 'test_runtime_catalog.py', 'test_block_test_runtime.py', 'test_block_test_stages.py', 'test_block_test_process.py', 'test_block_test_project_open.py', 'test_block_test_persistence.py', 'test_block_test_runtime_uncertainty.py', 'test_block_test_case_sessions.py', 'test_block_test_selection.py', 'test_block_test_stop.py')) {
        python (Join-Path $PSScriptRoot $test)
        if ($LASTEXITCODE -ne 0) { throw "Block-testing check failed: $test" }
    }
    foreach ($test in @('test_qualify_deployment.py', 'test_reliability.py', 'test_project_index_qualifier.py', 'test_guest_qualification.py', 'test_ssh_recovery.py', 'test_smoke_package.py')) {
        python -B (Join-Path $PSScriptRoot $test)
        if ($LASTEXITCODE -ne 0) { throw "Deployment/reliability check failed: $test" }
    }
    foreach ($test in @('test_typed_graphical_contracts.py', 'test_typed_graphical_instance_evidence.py', 'test_typed_graphical_qualification.py', 'test_typed_graphical_batch.py', 'test_typed_graphical_transport.py', 'test_typed_graphical_resolution_cases.py', 'test_typed_resolution_native_harness.py', 'test_typed_multi_dependency.py', 'test_typed_capture_prefix.py', 'test_typed_profile_staging.py')) {
        python -B (Join-Path $PSScriptRoot $test)
        if ($LASTEXITCODE -ne 0) { throw "Typed graphical qualification check failed: $test" }
    }
    & (Join-Path $PSScriptRoot 'Test-Libraries.ps1') -Configuration $Configuration
    python -B (Join-Path $PSScriptRoot 'test_component_source_reuse.py')
    if ($LASTEXITCODE -ne 0) { throw 'Component source reuse checks failed.' }
    dotnet "tests/TiaMcp.CapturedStorage.Tests/bin/$Configuration/net10.0/TiaMcp.CapturedStorage.Tests.dll"
    if ($LASTEXITCODE -ne 0) { throw 'Exact captured-storage qualification checks failed.' }
    foreach ($suite in @('TiaMcp.Hmi.Tests', 'TiaMcp.Hmi.Engineering.Tests', 'TiaMcp.Hmi.Model.Tests')) {
        dotnet "tests/$suite/bin/$Configuration/net10.0/$suite.dll"
        if ($LASTEXITCODE -ne 0) { throw "HMI validation failed: $suite" }
    }
    python -B (Join-Path $PSScriptRoot 'test_hmi_qualification.py')
    if ($LASTEXITCODE -ne 0) { throw 'HMI evidence harness validation failed.' }
    python -B (Join-Path $PSScriptRoot 'test_hmi_optional_startup.py')
    if ($LASTEXITCODE -ne 0) { throw 'HMI optional-startup evidence validation failed.' }
    python -B (Join-Path $PSScriptRoot 'test_review_explorer_acceptance.py')
    if ($LASTEXITCODE -ne 0) { throw 'Native review/explorer acceptance oracle validation failed.' }
    dotnet "tests/TiaMcp.HardwareEngineering.Tests/bin/$Configuration/net10.0/TiaMcp.HardwareEngineering.Tests.dll"
    if ($LASTEXITCODE -ne 0) { throw 'Hardware engineering preservation checks failed.' }
    python -B (Join-Path $PSScriptRoot 'test_remote_io_qualification.py')
    if ($LASTEXITCODE -ne 0) { throw 'Remote-I/O fixture qualification checks failed.' }
    dotnet "tests/TiaMcp.CompilationEvidence.Tests/bin/$Configuration/net10.0/TiaMcp.CompilationEvidence.Tests.dll"
    if ($LASTEXITCODE -ne 0) { throw 'Complete native compilation evidence checks failed.' }
    dotnet "tests/TiaMcp.EncoderEvidence.Tests/bin/$Configuration/net10.0/TiaMcp.EncoderEvidence.Tests.dll"
    if ($LASTEXITCODE -ne 0) { throw 'Encoder checkpoint evidence checks failed.' }
    dotnet "tests/TiaMcp.Deployment.Tests/bin/$Configuration/net10.0/TiaMcp.Deployment.Tests.dll" (Join-Path $testTemp ('project-index-store-tests-' + [Guid]::NewGuid().ToString('N') + '.json'))
    if ($LASTEXITCODE -ne 0) { throw 'Project index tests failed.' }
    dotnet "tests/TiaMcp.ProjectModel.Tests/bin/$Configuration/net10.0/TiaMcp.ProjectModel.Tests.dll"
    if ($LASTEXITCODE -ne 0) { throw 'Project model and portable explorer tests failed.' }
    node --test ui/project-explorer/tests/model.test.cjs ui/project-explorer/tests/client.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Explorer model/client checks failed.' }
    dotnet "tests/TiaMcp.Tests/bin/$Configuration/net10.0/TiaMcp.Tests.dll" --verify-worker "src/TiaMcp.Openness.V21/bin/$Configuration/net48/TiaMcp.Openness.V21.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Worker lifecycle regression check failed.' }
    python -B (Join-Path $PSScriptRoot 'verify_worker_probe_arguments.py') --worker "src/TiaMcp.Openness.V21/bin/$Configuration/net48/TiaMcp.Openness.V21.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Worker discovery probe argument checks failed.' }
    & "tests/TiaMcp.WatchAdapter.Boundary.Tests/bin/$Configuration/net48/TiaMcp.WatchAdapter.Boundary.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Production watch worker boundary checks failed.' }
    & "tests/TiaMcp.TestSuite.Worker.Tests/bin/$Configuration/net48/TiaMcp.TestSuite.Worker.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Test Suite owning-worker boundary checks failed.' }
    & "tests/TiaMcp.ComponentQualification.Boundary.Tests/bin/$Configuration/net48/TiaMcp.ComponentQualification.Boundary.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Component owning-worker boundary checks failed.' }
    foreach ($test in @('test_verify_watch_authoring.py', 'test_qualify_watch_authoring.py')) {
        python -B (Join-Path $PSScriptRoot $test)
        if ($LASTEXITCODE -ne 0) { throw "Watch authoring qualification check failed: $test" }
    }
    dotnet "tests/TiaMcp.Tests/bin/$Configuration/net10.0/TiaMcp.Tests.dll"
    if ($LASTEXITCODE -ne 0) { throw 'Bridge tests failed.' }
    & (Join-Path $repoRoot "src/TiaMcp.Simulator.V8/bin/$Configuration/net48/TiaMcp.Simulator.V8.exe") --self-test --self-test-directory (Join-Path $repoRoot 'artifacts')
    if ($LASTEXITCODE -ne 0) { throw 'Simulator validation tests failed.' }
} finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    Pop-Location
}
