param([string]$BundlePath, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ('artifacts/offline-package/' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Select a fresh evidence directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
$result = @{status='running';nativeExecuted=$false;steps=@()}
Start-Transcript -LiteralPath (Join-Path $output 'validation.log') | Out-Null
try {
    & (Join-Path $PSScriptRoot 'Test-Deployment.ps1')
    $result.steps += 'deployment guards'
    if (-not $BundlePath) { $BundlePath = & (Join-Path $PSScriptRoot 'Package.ps1') -Offline }
    $bundle = (Resolve-Path -LiteralPath $BundlePath).Path
    $verification = & (Join-Path $PSScriptRoot 'Verify-TiaMcpBundle.ps1') -BundlePath $bundle -Strict
    if ($verification.bundleKind -cne 'offline') { throw 'This test requires a portable bundle.' }
    $result.bundle = $bundle
    $result.manifestSha256 = $verification.manifestSha256
    $archive = $bundle + '.zip'
    $gitExecutable = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    # Extract our verified local package into a new path. Strict verification must
    # pass before any relocated script or executable is invoked.
    $relocated = Join-Path $output 'relocated Ãœber bundle'
    Expand-Archive -LiteralPath $archive -DestinationPath $relocated
    foreach ($path in @($bundle, $relocated)) {
        $label = if ($path -eq $bundle) { 'original' } else { 'relocated' }
        $verified = & (Join-Path $PSScriptRoot 'Verify-TiaMcpBundle.ps1') -BundlePath $path -Strict
        if ($verified.manifestSha256 -cne $result.manifestSha256) { throw 'Relocation changed the manifest.' }
        $config = Join-Path $output ($label + '.mcp.json')
        & (Join-Path $path 'scripts/New-McpConfiguration.ps1') -BundlePath $path -ArtifactsDirectory (Join-Path $output ($label + '-artifacts')) -GitExecutable $gitExecutable -StrictBundle |
            Set-Content -LiteralPath $config -Encoding UTF8
        python -B (Join-Path $PSScriptRoot 'smoke_offline_package.py') --bundle $path --configuration $config --archive $archive --output (Join-Path $output $label) --git-executable $gitExecutable
        if ($LASTEXITCODE -ne 0) { throw ('Portable smoke failed: ' + $label) }
        $result.steps += $label + ' packaged MCP and exact ZIP verification'
    }
    $packagedHost = Join-Path $relocated 'host/TiaMcp.Host.exe'
    $previousRelationshipEvidence = $env:TIA_RELATIONSHIP_EVIDENCE
    try {
        $env:TIA_RELATIONSHIP_EVIDENCE = Join-Path $output 'relationship-scope'
        dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.ProjectModel.Tests/TiaMcp.ProjectModel.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'project-model-build')
        if ($LASTEXITCODE -ne 0) { throw 'Saved relationship scope and store export checks failed.' }
        $result.steps += 'saved relationship scope and original store export for packaged viewer qualification'
    } finally { $env:TIA_RELATIONSHIP_EVIDENCE = $previousRelationshipEvidence }
    foreach ($workflow in @('graph','automationml','opcua','vci','translation')) {
        $arguments = @('-B', (Join-Path $PSScriptRoot ('test_' + $workflow + '_host_mcp.py')), '--host', $packagedHost, '--output', (Join-Path $output ($workflow + '-mcp')))
        if ($workflow -eq 'vci') { $arguments += @('--git-executable', $gitExecutable) }
        & python @arguments
        if ($LASTEXITCODE -ne 0) { throw ('Relocated portable workflow failed: ' + $workflow) }
        $result.steps += $workflow + ' packaged workflow'
    }
    $samples = Join-Path $output 'diagnostic-samples.json'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.Diagnostics.Tests/TiaMcp.Diagnostics.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'diagnostic-build') -- $samples
    if ($LASTEXITCODE -ne 0) { throw 'Diagnostic sample validation failed.' }
    python -B (Join-Path $PSScriptRoot 'qualify_trace_diagnostics_offline.py') --host $packagedHost --samples $samples --output (Join-Path $output 'diagnostic-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated portable diagnostic workflow failed.' }
    $result.steps += 'saved review, diagnostic watch and measurement packaged workflow'
    python -B (Join-Path $PSScriptRoot 'qualify_diagnostic_experiments_offline.py') --host $packagedHost --samples $samples --output (Join-Path $output 'diagnostic-experiment-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated diagnostic experiment composition workflow failed.' }
    $result.steps += 'diagnostic experiment before and after packaged workflow'
    python -B (Join-Path $PSScriptRoot 'qualify_repeated_diagnostic_experiments_offline.py') --host $packagedHost --samples $samples --output (Join-Path $output 'diagnostic-repeat-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated repeated diagnostic experiments and combined export failed.' }
    $result.steps += 'repeated diagnostic experiment and combined report packaged workflow'
    python -B (Join-Path $PSScriptRoot 'qualify_runtime_diagnostic_acquisition_offline.py') --host $packagedHost --samples $samples --output (Join-Path $output 'runtime-diagnostic-acquisition-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated retained runtime acquisition and diagnostic report workflow failed.' }
    $result.steps += 'retained runtime acquisition and diagnostic report packaged workflow'
    python -B (Join-Path $PSScriptRoot 'qualify_diagnostic_quality_offline.py') --host $packagedHost --samples $samples --output (Join-Path $output 'diagnostic-quality-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated diagnostic sample quality and clock epoch workflow failed.' }
    $result.steps += 'diagnostic sample quality and clock epoch packaged workflow'
    $testSuiteFixtures = Join-Path $output 'testsuite-fixtures'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.TestSuite.Tests/TiaMcp.TestSuite.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'testsuite-build') -- $testSuiteFixtures
    if ($LASTEXITCODE -ne 0) { throw 'Supplied Test Suite fixtures failed.' }
    python -B (Join-Path $PSScriptRoot 'test_testsuite_result_host.py') --host $packagedHost --fixture ($testSuiteFixtures + '-intake/mcp-fixture.json') --output (Join-Path $output 'testsuite-result-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated portable Test Suite result workflow failed.' }
    $result.steps += 'supplied Test Suite result packaged workflow'
    $unitFixtures = Join-Path $output 'software-units'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.SoftwareUnits.Tests/TiaMcp.SoftwareUnits.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'software-unit-build') -- $unitFixtures
    if ($LASTEXITCODE -ne 0) { throw 'Software-unit fixtures failed.' }
    python -B (Join-Path $PSScriptRoot 'test_software_unit_host.py') --host $packagedHost --fixture (Join-Path $unitFixtures 'mcp-fixture.json') --output (Join-Path $output 'software-unit-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated software-unit workflow failed.' }
    $result.steps += 'software-unit packaged workflow'
    python -B (Join-Path $PSScriptRoot 'test_teamcenter_review_host.py') --host $packagedHost --output (Join-Path $output 'teamcenter-review-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated Teamcenter observation workflow failed.' }
    $result.steps += 'Teamcenter observation packaged workflow'
    python -B (Join-Path $PSScriptRoot 'test_multiuser_review_host.py') --host $packagedHost --output (Join-Path $output 'multiuser-review-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated Multiuser observation workflow failed.' }
    $result.steps += 'Multiuser observation packaged workflow'
    python -B (Join-Path $PSScriptRoot 'test_addin_selection_host.py') --host $packagedHost --output (Join-Path $output 'addin-selection-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated Add-In selection workflow failed.' }
    $result.steps += 'Add-In selection packaged workflow'
    python -B (Join-Path $PSScriptRoot 'test_addin_receiver_host.py') --host $packagedHost --output (Join-Path $output 'addin-receiver-host')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated Add-In receiver enrollment gate failed.' }
    $result.steps += 'Add-In receiver production gate and pipe lifecycle'
    $connectionFixtures = Join-Path $output 'connections'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.Connections.Tests/TiaMcp.Connections.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'connection-build') -- $connectionFixtures
    if ($LASTEXITCODE -ne 0) { throw 'Connection fixtures failed.' }
    python -B (Join-Path $PSScriptRoot 'test_connection_host.py') --host $packagedHost --fixture (Join-Path $connectionFixtures 'mcp-fixture.json') --output (Join-Path $output 'connection-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated connection workflow failed.' }
    $result.steps += 'connection packaged workflow'
    $compositeFixtures = Join-Path $output 'composite-engineering'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.CompositeEngineering.Tests/TiaMcp.CompositeEngineering.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'composite-build') -- $compositeFixtures
    if ($LASTEXITCODE -ne 0) { throw 'Composite engineering fixtures failed.' }
    python -B (Join-Path $PSScriptRoot 'test_composite_engineering_contracts.py') --fixture $compositeFixtures
    if ($LASTEXITCODE -ne 0) { throw 'Independent composite source and interrupted publication checks failed.' }
    python -B (Join-Path $PSScriptRoot 'test_composite_engineering_host.py') --host $packagedHost --fixture (Join-Path $compositeFixtures 'mcp-fixture.json') --output (Join-Path $output 'composite-engineering-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated composite engineering workflow failed.' }
    $result.steps += 'composite engineering packaged workflow'
    python -B (Join-Path $PSScriptRoot 'test_composite_engineering_host.py') --host $packagedHost --fixture (Join-Path $compositeFixtures 'consumer-v2/mcp-fixture.json') --output (Join-Path $output 'composite-consumers-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated composite consumer context failed.' }
    python -B (Join-Path $PSScriptRoot 'test_composite_engineering_compatibility.py') --host $packagedHost --output (Join-Path $output 'composite-prior-v1-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated actual prior composite v1 compatibility failed.' }
    $result.steps += 'composite consumer and retained-v1 packaged workflow'
    python -B (Join-Path $PSScriptRoot 'test_composite_typed_contracts.py') --fixture (Join-Path $compositeFixtures 'typed-v3')
    if ($LASTEXITCODE -ne 0) { throw 'Independent packaged typed composite verification failed.' }
    python -B (Join-Path $PSScriptRoot 'test_composite_engineering_host.py') --host $packagedHost --fixture (Join-Path $compositeFixtures 'typed-v3/mcp-fixture.json') --output (Join-Path $output 'composite-typed-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated typed composite mapping workflow failed.' }
    python -B (Join-Path $PSScriptRoot 'test_composite_engineering_compatibility.py') --host $packagedHost --fixture (Join-Path $repoRoot 'fixtures/composite-engineering/retained-v1-v2') --output (Join-Path $output 'composite-prior-v1-v2-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated actual prior composite v1/v2 compatibility failed.' }
    $result.steps += 'composite typed mappings and retained-v1-v2 packaged workflow'
    $hmiComponentFixtures = Join-Path $output 'hmi-components'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.HmiComponents.Tests/TiaMcp.HmiComponents.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'hmi-component-build') -- $hmiComponentFixtures
    if ($LASTEXITCODE -ne 0) { throw 'Component/HMI fixtures failed.' }
    python -B (Join-Path $PSScriptRoot 'test_hmi_component_host.py') --host $packagedHost --fixture (Join-Path $hmiComponentFixtures 'mcp-fixture.json') --output (Join-Path $output 'hmi-component-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated component/HMI workflow failed.' }
    python -B (Join-Path $PSScriptRoot 'test_hmi_text_list_generation_host.py') --host $packagedHost --fixture (Join-Path $hmiComponentFixtures 'text-generation/mcp-fixture.json') --output (Join-Path $output 'hmi-text-generation-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated bound HMI text-list generation workflow failed.' }
    $result.steps += 'bound HMI text-list generation packaged workflow'
    python -B (Join-Path $PSScriptRoot 'test_hmi_numeric_generation_host.py') --host $packagedHost --fixture (Join-Path $hmiComponentFixtures 'text-numeric/mcp-fixture.json') --output (Join-Path $output 'hmi-numeric-generation-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated numeric HMI state generation workflow failed.' }
    $result.steps += 'numeric HMI state generation packaged workflow'
    python -B (Join-Path $PSScriptRoot 'test_hmi_coordinated_generation_host.py') --host $packagedHost --fixture (Join-Path $hmiComponentFixtures 'text-coordinated/mcp-fixture.json') --output (Join-Path $output 'hmi-coordinated-generation-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated coordinated HMI generation-set workflow failed.' }
    python -B (Join-Path $PSScriptRoot 'qualify_hmi_coordinated_prior.py') --host $packagedHost --output (Join-Path $output 'hmi-coordinated-prior')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated actual prior text-list author compatibility failed.' }
    $result.steps += 'coordinated HMI output sets and prior author compatibility'
    python -B (Join-Path $PSScriptRoot 'test_hmi_set_evolution_host.py') --host $packagedHost --fixture (Join-Path $hmiComponentFixtures 'text-evolution/mcp-fixture.json') --output (Join-Path $output 'hmi-set-evolution-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated explicit HMI generation-set evolution workflow failed.' }
    python -B (Join-Path $PSScriptRoot 'qualify_hmi_set_evolution_prior.py') --host $packagedHost --output (Join-Path $output 'hmi-set-evolution-prior')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated actual prior HMI generation-set compatibility failed.' }
    $result.steps += 'HMI set evolution and retained v1 generation-set compatibility'
    python -B (Join-Path $PSScriptRoot 'test_hmi_integer_conversion_host.py') --host $packagedHost --fixture (Join-Path $hmiComponentFixtures 'text-conversion/mcp-fixture.json') --output (Join-Path $output 'hmi-integer-conversion-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated explicit integer text-list conversion workflow failed.' }
    $result.steps += 'explicit integer text-list conversion and original lineage packaged workflow'
    python -B (Join-Path $PSScriptRoot 'hmi_derived_effect_checks.py') --fixture (Join-Path $output 'hmi-component-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated independent HMI expected-effect checks failed.' }
    python -B (Join-Path $PSScriptRoot 'test_hmi_component_compatibility.py') --host $packagedHost --output (Join-Path $output 'hmi-component-prior-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated prior HMI record compatibility failed.' }
    $result.steps += 'component/HMI packaged workflow'
    $snapshotFixtures = Join-Path $output 'db-snapshots'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.DbSnapshots.Tests/TiaMcp.DbSnapshots.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'db-snapshot-build') -- $snapshotFixtures
    if ($LASTEXITCODE -ne 0) { throw 'DB snapshot fixtures failed.' }
    python -B (Join-Path $PSScriptRoot 'test_db_snapshot_host.py') --host $packagedHost --fixture (Join-Path $snapshotFixtures 'mcp-fixture.json') --output (Join-Path $output 'db-snapshot-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated DB snapshot workflow failed.' }
    $result.steps += 'DB snapshot packaged workflow'
    $safetyFixtures = Join-Path $output 'safety-evidence'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.SafetyEvidence.Tests/TiaMcp.SafetyEvidence.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'safety-evidence-build') -- $safetyFixtures
    if ($LASTEXITCODE -ne 0) { throw 'Safety evidence fixtures failed.' }
    python -B (Join-Path $PSScriptRoot 'test_safety_evidence_host.py') --host $packagedHost --fixture (Join-Path $safetyFixtures 'mcp-fixture.json') --output (Join-Path $output 'safety-evidence-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated safety evidence workflow failed.' }
    $result.steps += 'safety evidence packaged workflow'
    python -B (Join-Path $PSScriptRoot 'test_manifest_publication_host.py') --host $packagedHost --output (Join-Path $output 'manifest-publication-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated manifest publication recovery workflow failed.' }
    $result.steps += 'manifest publication recovery packaged workflow'
    $driveFixtures = Join-Path $output 'drive-profiles'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.DriveProfiles.Tests/TiaMcp.DriveProfiles.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'drive-profile-build') -- $driveFixtures
    if ($LASTEXITCODE -ne 0) { throw 'Drive profile fixtures failed.' }
    python -B (Join-Path $PSScriptRoot 'test_drive_profile_host.py') --host $packagedHost --fixture (Join-Path $driveFixtures 'mcp-fixture.json') --output (Join-Path $output 'drive-profile-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated drive profile workflow failed.' }
    $result.steps += 'drive profile packaged workflow'
    python -B (Join-Path $PSScriptRoot 'test_drive_telegram_contracts.py') --fixture (Join-Path $driveFixtures 'telegrams-v2')
    if ($LASTEXITCODE -ne 0) { throw 'Typed telegram independent input/schema checks failed.' }
    python -B (Join-Path $PSScriptRoot 'test_drive_profile_host.py') --host $packagedHost --fixture (Join-Path $driveFixtures 'telegrams-v2/mcp-fixture.json') --output (Join-Path $output 'drive-telegrams-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated typed telegram workflow failed.' }
    $result.steps += 'typed telegram packaged workflow'
    $technologyFixtures = Join-Path $output 'technology-profiles'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.TechnologyProfiles.Tests/TiaMcp.TechnologyProfiles.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'technology-profile-build') -- $technologyFixtures
    if ($LASTEXITCODE -ne 0) { throw 'Technology profile fixtures failed.' }
    python -B (Join-Path $PSScriptRoot 'test_technology_profile_host.py') --host $packagedHost --fixture (Join-Path $technologyFixtures 'mcp-fixture.json') --output (Join-Path $output 'technology-profile-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated technology profile workflow failed.' }
    $result.steps += 'technology profile packaged workflow'
    python -B (Join-Path $PSScriptRoot 'test_technology_encoder_contracts.py') --fixture (Join-Path $technologyFixtures 'encoders-v4')
    if ($LASTEXITCODE -ne 0) { throw 'Typed encoder independent source/schema checks failed.' }
    python -B (Join-Path $PSScriptRoot 'test_technology_profile_compatibility.py') --host $packagedHost --output (Join-Path $output 'technology-prior-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Prior technology record packaged compatibility failed.' }
    $result.steps += 'prior technology record compatibility'
    $accessFixtures = Join-Path $output 'access'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.Access.Tests/TiaMcp.Access.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'access-build') -- $accessFixtures
    if ($LASTEXITCODE -ne 0) { throw 'Public access fixtures failed.' }
    python -B (Join-Path $PSScriptRoot 'test_access_host.py') --host $packagedHost --fixture (Join-Path $accessFixtures 'mcp-fixture.json') --output (Join-Path $output 'access-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated public access workflow failed.' }
    $result.steps += 'public access packaged workflow'
    python -B (Join-Path $PSScriptRoot 'test_runtime_handoff_contracts.py') --fixture (Join-Path $accessFixtures 'handoff')
    if ($LASTEXITCODE -ne 0) { throw 'Packaged security/runtime handoff original-evidence checks failed.' }
    python -B (Join-Path $PSScriptRoot 'test_access_compatibility.py') --host $packagedHost --output (Join-Path $output 'access-prior-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Packaged prior access record compatibility failed.' }
    $result.steps += 'security/runtime handoff originals and prior access compatibility'
    python -B (Join-Path $PSScriptRoot 'test_runtime_handoff_workflow.py') --fixture (Join-Path $accessFixtures 'handoff-workflow')
    if ($LASTEXITCODE -ne 0) { throw 'Packaged retained handoff original-evidence checks failed.' }
    python -B (Join-Path $PSScriptRoot 'test_runtime_handoff_host.py') --host $packagedHost --fixture (Join-Path $accessFixtures 'handoff-workflow/mcp-fixture.json') --output (Join-Path $output 'runtime-handoff-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated security/runtime handoff MCP workflow failed.' }
    $result.steps += 'retained security/runtime handoff MCP workflow'
    $certificateFixtures = Join-Path $output 'public-certificates'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.PublicCertificates.Tests/TiaMcp.PublicCertificates.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'certificate-build') -- $certificateFixtures
    if ($LASTEXITCODE -ne 0) { throw 'Public certificate fixtures failed.' }
    python -B (Join-Path $PSScriptRoot 'test_public_certificate_host.py') --host $packagedHost --fixture (Join-Path $certificateFixtures 'mcp-fixture.json') --output (Join-Path $output 'public-certificate-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated public certificate workflow failed.' }
    $result.steps += 'public certificate packaged workflow'
    $webFixtures = Join-Path $output 'web-applications'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.WebApplications.Tests/TiaMcp.WebApplications.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'web-build') -- $webFixtures
    if ($LASTEXITCODE -ne 0) { throw 'Web application fixtures failed.' }
    python -B (Join-Path $PSScriptRoot 'test_web_application_host.py') --host $packagedHost --fixture (Join-Path $webFixtures 'mcp-fixture.json') --output (Join-Path $output 'web-application-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated web application workflow failed.' }
    $result.steps += 'web application packaged workflow'
    $externalFixtures = Join-Path $output 'external-resources'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.Engineering.Tests/TiaMcp.Engineering.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'external-build') -- --external-only $externalFixtures
    if ($LASTEXITCODE -ne 0) { throw 'External resource fixtures failed.' }
    python -B (Join-Path $PSScriptRoot 'test_external_resource_host.py') --host $packagedHost --fixture (Join-Path $externalFixtures 'mcp-fixture.json') --output (Join-Path $output 'external-resource-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated external resource status failed.' }
    $result.steps += 'external resource packaged workflow'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.Credentials.Tests/TiaMcp.Credentials.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'credential-build') -- (Join-Path $output 'credentials')
    if ($LASTEXITCODE -ne 0) { throw 'Protected credential storage checks failed.' }
    python -B (Join-Path $PSScriptRoot 'test_credential_cli.py') --host $packagedHost --output (Join-Path $output 'credential-cli')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated protected credential commands failed.' }
    $result.steps += 'protected credential local commands'
    python -B (Join-Path $PSScriptRoot 'test_credential_operation_host.py') --host $packagedHost --fixture (Join-Path $output 'credentials/operations/mcp-fixture.json') --output (Join-Path $output 'credential-operation-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated credential operation status checks failed.' }
    $result.steps += 'credential operation ownership and status'
    python -B (Join-Path $PSScriptRoot 'test_credential_operation_host.py') --host $packagedHost --fixture (Join-Path $output 'credentials/recovery/mcp-fixture.json') --output (Join-Path $output 'credential-recovery-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated credential recovery status checks failed.' }
    $result.steps += 'credential recovery session and status'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.PhysicalRuntime.Tests/TiaMcp.PhysicalRuntime.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'runtime-build') -- --tls-fixtures (Join-Path $output 'runtime-tls')
    if ($LASTEXITCODE -ne 0) { throw 'Runtime TLS fixtures failed.' }
    python -B (Join-Path $PSScriptRoot 'qualify_runtime_profiles.py') --assembly (Join-Path $output 'runtime-build/bin/TiaMcp.PhysicalRuntime.Tests/release/TiaMcp.PhysicalRuntime.Tests.dll') --tls (Join-Path $output 'runtime-tls') --runtime-directory (Split-Path $packagedHost -Parent) --output (Join-Path $output 'runtime-profiles')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated advertised runtime profile and refusal qualification failed.' }
    $result.steps += 'advertised runtime profile and refusal packaged qualification'
    python -B (Join-Path $PSScriptRoot 'test_runtime_session_host.py') --host $packagedHost --tls (Join-Path $output 'runtime-tls') --output (Join-Path $output 'runtime-session-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated runtime session workflow failed.' }
    python -B (Join-Path $PSScriptRoot 'test_runtime_handoff_lifecycle.py') --fixture (Join-Path $output 'runtime-session-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated bound lifecycle independent verification failed.' }
    $result.steps += 'runtime session TLS and MCP workflow'
    python -B (Join-Path $PSScriptRoot 'test_runtime_capture_compatibility.py') --host $packagedHost --output (Join-Path $output 'runtime-capture-compatibility-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated prior capture compatibility failed.' }
    $result.steps += 'actual prior capture compatibility'
    dotnet run --project (Join-Path $repoRoot 'tests/TiaMcp.OpcUa.Runtime.Tests/TiaMcp.OpcUa.Runtime.Tests.csproj') -c Release --artifacts-path (Join-Path $output 'opcua-runtime-build') -- --output (Join-Path $output 'opcua-runtime')
    if ($LASTEXITCODE -ne 0) { throw 'Secure OPC UA local-server qualification failed.' }
    python -B (Join-Path $PSScriptRoot 'qualify_opcua_runtime.py') --host $packagedHost --server (Join-Path $output 'opcua-runtime-build/bin/TiaMcp.OpcUa.Runtime.Tests/release/TiaMcp.OpcUa.Runtime.Tests.dll') --output (Join-Path $output 'opcua-runtime-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated secure OPC UA local-server/MCP qualification failed.' }
    $result.steps += 'secure OPC UA local-server and packaged MCP qualification'
    python -B (Join-Path $PSScriptRoot 'qualify_opcua_capture.py') --host $packagedHost --server (Join-Path $output 'opcua-runtime-build/bin/TiaMcp.OpcUa.Runtime.Tests/release/TiaMcp.OpcUa.Runtime.Tests.dll') --output (Join-Path $output 'opcua-capture-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated OPC UA capture lifecycle and retained export qualification failed.' }
    $result.steps += 'OPC UA polling capture, interrupted reopen and exact export packaged workflow'
    python -B (Join-Path $PSScriptRoot 'qualify_opcua_subscription.py') --host $packagedHost --server (Join-Path $output 'opcua-runtime-build/bin/TiaMcp.OpcUa.Runtime.Tests/release/TiaMcp.OpcUa.Runtime.Tests.dll') --output (Join-Path $output 'opcua-subscription-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated OPC UA subscription acquisition and retained export qualification failed.' }
    $result.steps += 'OPC UA subscription, sequence evidence and interrupted reopen packaged workflow'
    python -B (Join-Path $PSScriptRoot 'qualify_diagnostic_ua_binding.py') --host $packagedHost --server (Join-Path $output 'opcua-runtime-build/bin/TiaMcp.OpcUa.Runtime.Tests/release/TiaMcp.OpcUa.Runtime.Tests.dll') --samples $samples --output (Join-Path $output 'diagnostic-ua-binding-mcp')
    if ($LASTEXITCODE -ne 0) { throw 'Relocated prebound UA diagnostic intent workflow failed.' }
    $result.steps += 'prebound UA intent, retained observations, adverse quality/sequence evidence, readable exports and restart packaged workflow'


    & (Join-Path $PSScriptRoot 'Verify-TiaMcpBundle.ps1') -BundlePath $relocated -Strict | Out-Host
    $result.status = 'passed'
} catch {
    $result.status = 'failed'
    $result.failure = $_.Exception.Message
    throw
} finally {
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8
    Stop-Transcript | Out-Null
}
