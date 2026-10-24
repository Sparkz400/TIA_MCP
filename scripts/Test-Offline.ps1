param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [ValidateSet('All','Python','Managed')][string]$Suite = 'All',
    # CI splits the managed suites across parallel runners; the default runs every suite.
    [ValidateRange(1, 16)][int]$ShardCount = 1,
    [ValidateRange(0, 15)][int]$ShardIndex = 0
)
if ($ShardIndex -ge $ShardCount) { throw 'ShardIndex must be below ShardCount.' }
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot ('artifacts/offline-validation/' + [Guid]::NewGuid().ToString('N'))
}
$output = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }))
if (-not $output.StartsWith($repoRoot.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Offline test output must be under this source snapshot so fixture discovery cannot select another checkout.'
}
for ($ancestor = [IO.DirectoryInfo]::new($output); $ancestor -and $ancestor.FullName -ine $repoRoot; $ancestor = $ancestor.Parent) {
    if (Test-Path -LiteralPath (Join-Path $ancestor.FullName 'TiaMcp.slnx')) {
        throw 'Offline test output crosses another source snapshot; select a directory directly under this repository artifacts tree.'
    }
}
if (Test-Path -LiteralPath $output) { throw 'Select a fresh output directory; previous evidence is immutable.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$buildOutput = Join-Path $output 'dotnet'
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
# Measured hosted-runner seconds per managed suite; suites missing here count as 60 s.
$ManagedSuiteSeconds = @{}
# Deterministic longest-first split, so every suite runs in exactly one shard.
function Select-ManagedShard([string[]]$names, [int]$count, [int]$index) {
    if ($count -eq 1) { return $names }
    $weight = { param($n) if ($ManagedSuiteSeconds.ContainsKey($n)) { [double]$ManagedSuiteSeconds[$n] } else { 60.0 } }
    $loads = [double[]]::new($count); $assigned = @{}
    foreach ($name in ($names | Sort-Object @{Expression={ & $weight $_ }; Descending=$true}, @{Expression={ $_ }})) {
        $target = 0
        for ($i = 1; $i -lt $count; $i++) { if ($loads[$i] -lt $loads[$target]) { $target = $i } }
        $loads[$target] += & $weight $name; $assigned[$name] = $target
    }
    return @($names | Where-Object { $assigned[$_] -eq $index })
}
$result = @{schemaVersion=1;status='running';suite=$Suite;startedAtUtc=[DateTime]::UtcNow.ToString('O');nativeQualification=$false;managedSuites=@()}
Push-Location $repoRoot
try {
    $env:TEMP = Join-Path $output 'temp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
    Start-Transcript -Path (Join-Path $output 'validation.log') | Out-Null
    if ($Suite -in @('All','Python')) {
        python -B scripts/test_contracts.py
        if ($LASTEXITCODE -ne 0) { throw 'Feature contract validation failed.' }
        $result.contracts = 'passed'
        python -B tests/ProjectModelContracts.py
        if ($LASTEXITCODE -ne 0) { throw 'Project model and relationship scope schema checks failed.' }
        $result.projectModelContracts = 'passed'
        python -B scripts/api_coverage_bindings.py validate contracts/api-coverage/implementation-bindings.v1.json --allow-missing-evidence
        if ($LASTEXITCODE -ne 0) { throw 'Source binding validation failed.' }
        $result.sourceBindings = 'passed'
        python -B scripts/audit_retained_evidence.py --out (Join-Path $output 'retained-evidence.json')
        if ($LASTEXITCODE -ne 0) { throw 'Retained evidence integrity audit failed.' }
        $result.retainedEvidence = 'inventory-written; missing inputs remain unverified'
        python -B -m unittest discover -s scripts -p 'test_*.py'
        if ($LASTEXITCODE -ne 0) { throw 'Python offline tests failed.' }
        $result.python = 'passed'
    }
    if ($Suite -eq 'All') {
        node --test ui/project-explorer/tests/model.test.cjs ui/project-explorer/tests/client.test.cjs
        if ($LASTEXITCODE -ne 0) { throw 'Explorer model/client tests failed.' }
        $result.explorerModelClient = 'passed'
    }
    # Explicit allowlist: the net48 component boundary suite references the native
    # Siemens worker and belongs to Test.ps1 on an engineering workstation.
    $suites = @(
        'TiaMcp.PhysicalRuntime.Tests',
        'TiaMcp.OpcUa.Runtime.Tests',
        'TiaMcp.Engineering.Tests', 'TiaMcp.Credentials.Tests', 'TiaMcp.WatchTables.Tests', 'TiaMcp.VersionControl.Tests', 'TiaMcp.Translations.Tests', 'TiaMcp.TestSuite.Tests',
        'TiaMcp.VersionControl.Workflow.Tests',
        'TiaMcp.TranslationHost.Tests',
        'TiaMcp.TranslationImport.Tests',
        'TiaMcp.ComponentWindow.Tests', 'TiaMcp.Graph.Tests', 'TiaMcp.Graph.Workflow.Tests', 'TiaMcp.SoftwareUnits.Tests', 'TiaMcp.Connections.Tests', 'TiaMcp.CompositeEngineering.Tests', 'TiaMcp.HmiComponents.Tests', 'TiaMcp.DbSnapshots.Tests', 'TiaMcp.SafetyEvidence.Tests', 'TiaMcp.DriveProfiles.Tests', 'TiaMcp.TechnologyProfiles.Tests', 'TiaMcp.Access.Tests', 'TiaMcp.PublicCertificates.Tests', 'TiaMcp.WebApplications.Tests', 'TiaMcp.AutomationMl.Tests', 'TiaMcp.HmiRepairAdmission.Tests',
        'TiaMcp.OpcUa.Tests', 'TiaMcp.OpcUa.Dependencies.Tests', 'TiaMcp.OpcUa.Workflow.Tests',
        'TiaMcp.Diagnostics.Tests', 'TiaMcp.Inventory.Tests',
        'TiaMcp.Tests', 'TiaMcp.Deployment.Tests', 'TiaMcp.Library.Tests',
        'TiaMcp.LibraryHmi.Tests', 'TiaMcp.PlcLibraryIntegration.Tests',
        'TiaMcp.CapturedStorage.Tests', 'TiaMcp.ProjectModel.Tests',
        'TiaMcp.Hmi.Tests', 'TiaMcp.Hmi.Engineering.Tests',
        'TiaMcp.Hmi.Model.Tests', 'TiaMcp.HardwareEngineering.Tests',
        'TiaMcp.CompilationEvidence.Tests', 'TiaMcp.EncoderEvidence.Tests'
    )
    if ($Suite -in @('All','Managed')) {
        $suites = @(Select-ManagedShard $suites $ShardCount $ShardIndex)
        $result.managedShard = @{index=$ShardIndex;count=$ShardCount;suites=$suites}
        $result.managedSuiteSeconds = [ordered]@{}
        dotnet build src/TiaMcp.OpcUa.Cli/TiaMcp.OpcUa.Cli.csproj --configuration $Configuration --artifacts-path $buildOutput
        if ($LASTEXITCODE -ne 0) { throw 'OPC UA CLI build failed.' }
        $gitExecutable = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        # Build every suite once, in parallel, through a generated solution; each suite then runs with --no-build.
        $solutionDirectory = Join-Path $output 'managed-suites'
        dotnet new sln --name managed-suites --output $solutionDirectory | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Managed suite solution could not be created.' }
        $solution = (Get-ChildItem -LiteralPath $solutionDirectory -File | Where-Object { $_.Name -like 'managed-suites.sln*' } | Select-Object -First 1).FullName
        dotnet sln $solution add @($suites | ForEach-Object { "tests/$_/$_.csproj" }) | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Managed suites could not be added to the build solution.' }
        dotnet build $solution --configuration $Configuration --artifacts-path $buildOutput
        if ($LASTEXITCODE -ne 0) { throw 'Managed suite build failed.' }
        foreach ($managedSuite in $suites) {
            $suiteClock = [Diagnostics.Stopwatch]::StartNew()
            if ($managedSuite -eq 'TiaMcp.PhysicalRuntime.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'physical-runtime-results.json')
            } elseif ($managedSuite -eq 'TiaMcp.OpcUa.Runtime.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- --output (Join-Path $output 'opcua-runtime')
            } elseif ($managedSuite -eq 'TiaMcp.Deployment.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'project-index-store-tests.json')
            } elseif ($managedSuite -eq 'TiaMcp.Diagnostics.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'diagnostic-samples.json')
            } elseif ($managedSuite -eq 'TiaMcp.Credentials.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'credentials')
            } elseif ($managedSuite -eq 'TiaMcp.Engineering.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- --external-output (Join-Path $output 'external-resources')
            } elseif ($managedSuite -eq 'TiaMcp.WebApplications.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'web-applications')
            } elseif ($managedSuite -eq 'TiaMcp.PublicCertificates.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'public-certificates')
            } elseif ($managedSuite -eq 'TiaMcp.Access.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'access')
            } elseif ($managedSuite -eq 'TiaMcp.DriveProfiles.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'drive-profiles')
            } elseif ($managedSuite -eq 'TiaMcp.TechnologyProfiles.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'technology-profiles')
            } elseif ($managedSuite -eq 'TiaMcp.SafetyEvidence.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'safety-evidence')
            } elseif ($managedSuite -eq 'TiaMcp.DbSnapshots.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'db-snapshots')
            } elseif ($managedSuite -eq 'TiaMcp.Connections.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'connections')
            } elseif ($managedSuite -eq 'TiaMcp.CompositeEngineering.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'composite-engineering')
            } elseif ($managedSuite -eq 'TiaMcp.HmiComponents.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'hmi-components')
            } elseif ($managedSuite -eq 'TiaMcp.SoftwareUnits.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'software-units')
            } elseif ($managedSuite -eq 'TiaMcp.AutomationMl.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'automationml-workflow')
            } elseif ($managedSuite -eq 'TiaMcp.Graph.Workflow.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'graph-workflow')
            } elseif ($managedSuite -eq 'TiaMcp.TranslationHost.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'translation-host')
            } elseif ($managedSuite -eq 'TiaMcp.TranslationImport.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'translation-import')
            } elseif ($managedSuite -eq 'TiaMcp.HmiRepairAdmission.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'hmi-admission')
            } elseif ($managedSuite -eq 'TiaMcp.TestSuite.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'testsuite')
            } elseif ($managedSuite -eq 'TiaMcp.VersionControl.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- --git-executable $gitExecutable --git-fixtures (Join-Path $output 'git')
            } elseif ($managedSuite -eq 'TiaMcp.VersionControl.Workflow.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- $gitExecutable (Join-Path $output 'vci-workflow')
            } elseif ($managedSuite -eq 'TiaMcp.EncoderEvidence.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $output 'encoder-checkpoint')
            } elseif ($managedSuite -eq 'TiaMcp.OpcUa.Dependencies.Tests') {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput -- (Join-Path $buildOutput "bin/TiaMcp.OpcUa.Cli/$($Configuration.ToLowerInvariant())/TiaMcp.OpcUa.Cli.dll")
            } elseif ($managedSuite -eq 'TiaMcp.Tests') {
                # Transcripts can omit redirected native stdout; retain the fixture inventory explicitly.
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput 2>&1 |
                    Tee-Object -FilePath (Join-Path $output 'general-managed.log')
            } elseif ($managedSuite -eq 'TiaMcp.ProjectModel.Tests') {
                $previousRelationshipEvidence = $env:TIA_RELATIONSHIP_EVIDENCE
                try {
                    $env:TIA_RELATIONSHIP_EVIDENCE = Join-Path $output 'relationship-scope'
                    dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput
                } finally { $env:TIA_RELATIONSHIP_EVIDENCE = $previousRelationshipEvidence }
            } else {
                dotnet run --no-build --project "tests/$managedSuite/$managedSuite.csproj" --configuration $Configuration --artifacts-path $buildOutput
            }
            if ($LASTEXITCODE -ne 0) { throw "Managed offline tests failed: $managedSuite" }
            $result.managedSuites += $managedSuite
            $result.managedSuiteSeconds[$managedSuite] = [math]::Round($suiteClock.Elapsed.TotalSeconds, 1)
        }
        if ($Suite -eq 'All') {
            $previousRelationshipEvidence = $env:TIA_RELATIONSHIP_EVIDENCE
            $previousExplorerModel = $env:TIA_EXPLORER_MODEL
            try {
                $env:TIA_RELATIONSHIP_EVIDENCE = Join-Path $output 'relationship-scope'
                $env:TIA_EXPLORER_MODEL = Join-Path $repoRoot 'ui/project-explorer/model.js'
                node --test ui/project-explorer/tests/relationship-scope.test.cjs
                if ($LASTEXITCODE -ne 0) { throw 'Independent saved relationship scope checks failed.' }
                node ui/project-explorer/tests/relationship-benchmark.cjs (Join-Path $output 'relationship-scale.json')
                if ($LASTEXITCODE -ne 0) { throw 'Bounded relationship scale qualification failed.' }
                $result.relationshipScope = 'passed; independent expected edges, C# export/reopen and 1k/10k/50k retained snapshots'
            } finally {
                $env:TIA_RELATIONSHIP_EVIDENCE = $previousRelationshipEvidence
                $env:TIA_EXPLORER_MODEL = $previousExplorerModel
            }
        }
        python -B scripts/test_composite_engineering_contracts.py --fixture (Join-Path $output 'composite-engineering')
        if ($LASTEXITCODE -ne 0) { throw 'Composite engineering independent input/schema check failed.' }
        $result.compositeEngineering = 'passed; v1 mapping and v2 consumer context fixtures with independent original-source checks; no native qualification'
        & (Join-Path $PSScriptRoot 'Test-AddInOwner.ps1') -OutputDirectory (Join-Path $output 'addin-owner-framework')
        python -B scripts/test_addin_handoff.py --log (Join-Path $output 'general-managed.log') --output (Join-Path $output 'addin-handoff-lifecycle.json')
        if ($LASTEXITCODE -ne 0) { throw 'Add-In handoff independent receipt-chain checks failed.' }
        $result.addInHandoffLifecycle = 'passed; internal lifecycle with synthetic authority, no transport or production enrollment'
        python -B scripts/test_addin_receiver.py --log (Join-Path $output 'general-managed.log') --output (Join-Path $output 'addin-receiver-processes.json')
        if ($LASTEXITCODE -ne 0) { throw 'Add-In receiver independent process evidence checks failed.' }
        $result.addInReceiverProcesses = 'passed; actual Windows processes and pipes, synthetic enrollment only'
        python -B scripts/test_addin_owner_context.py --fixture (Join-Path $output 'addin-owner-framework/result.json')
        if ($LASTEXITCODE -ne 0) { throw 'Add-In owner Framework/schema check failed.' }
        $result.addInOwnerContext = 'passed; actual OS handles and Framework runtime, synthetic native-ID route'
        # Real stdio Host workflow, deliberately absent native workers. No native
        # queue ownership is needed; startup of a Siemens worker would fail.
        python -B scripts/test_physical_runtime_contracts.py --fixture (Join-Path $output 'physical-runtime-results.json')
        if ($LASTEXITCODE -ne 0) { throw 'Physical runtime protocol contract cross-check failed.' }
        $result.physicalRuntimeContracts = 'passed; protocol only, no network or native execution'
        python -B scripts/runtime_pressure_checks.py (Join-Path $output 'physical-runtime-results.json.polling/pressure')
        if ($LASTEXITCODE -ne 0) { throw 'Independent runtime pressure chain/value/schedule checks failed.' }
        $result.runtimePressure = 'passed; bounded load and capacity, actual disk-full remains a separate prerequisite'
        dotnet (Join-Path $buildOutput "bin/TiaMcp.PhysicalRuntime.Tests/$($Configuration.ToLowerInvariant())/TiaMcp.PhysicalRuntime.Tests.dll") --tls-fixtures (Join-Path $output 'runtime-tls')
        if ($LASTEXITCODE -ne 0) { throw 'Runtime TLS fixture generation failed.' }
        python -B scripts/qualify_runtime_profiles.py --assembly (Join-Path $buildOutput "bin/TiaMcp.PhysicalRuntime.Tests/$($Configuration.ToLowerInvariant())/TiaMcp.PhysicalRuntime.Tests.dll") --tls (Join-Path $output 'runtime-tls') --runtime-directory (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())") --output (Join-Path $output 'runtime-profiles')
        if ($LASTEXITCODE -ne 0) { throw 'Advertised runtime profile and refusal qualification failed.' }
        $result.runtimeProfiles = 'passed; exact Host client binaries and pinned loopback fixtures, no device qualification'
        python -B scripts/test_runtime_session_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --tls (Join-Path $output 'runtime-tls') --output (Join-Path $output 'runtime-session-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Runtime session TLS/MCP workflow failed.' }
        $result.runtimeSessionMcp = 'passed; loopback TLS and DPAPI, no physical PLC'
        python -B scripts/test_runtime_handoff_lifecycle.py --fixture (Join-Path $output 'runtime-session-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Bound runtime lifecycle independent verification failed.' }
        $result.runtimeHandoffLifecycle = 'passed; original capture chains, migration inputs and TLS/process observations'
        python -B scripts/test_runtime_capture_compatibility.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'runtime-capture-compatibility-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Prior runtime capture compatibility failed.' }
        $result.runtimeCaptureCompatibility = 'passed; actual prior producer bytes'
        python -B scripts/qualify_opcua_runtime.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --server (Join-Path $buildOutput "bin/TiaMcp.OpcUa.Runtime.Tests/$($Configuration.ToLowerInvariant())/TiaMcp.OpcUa.Runtime.Tests.dll") --output (Join-Path $output 'opcua-runtime-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Secure OPC UA local-server/MCP qualification failed.' }
        $result.opcUaRuntimeMcp = 'passed; exact Host runtime and explicit local server, no physical target qualification'
        python -B scripts/qualify_opcua_capture.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --server (Join-Path $buildOutput "bin/TiaMcp.OpcUa.Runtime.Tests/$($Configuration.ToLowerInvariant())/TiaMcp.OpcUa.Runtime.Tests.dll") --output (Join-Path $output 'opcua-capture-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'OPC UA capture lifecycle and retained export qualification failed.' }
        $result.opcUaCaptureMcp = 'passed; actual encrypted loopback acquisition, quality/time preservation and interrupted capture reopen'
        python -B scripts/qualify_opcua_subscription.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --server (Join-Path $buildOutput "bin/TiaMcp.OpcUa.Runtime.Tests/$($Configuration.ToLowerInvariant())/TiaMcp.OpcUa.Runtime.Tests.dll") --output (Join-Path $output 'opcua-subscription-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'OPC UA subscription acquisition workflow failed.' }
        $result.opcUaSubscriptionMcp = 'passed; encrypted loopback subscription, original sequence evidence and interrupted reopen without replay'
        python -B scripts/test_teamcenter_review_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'teamcenter-review-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Teamcenter observation workflow failed.' }
        python -B scripts/test_multiuser_review_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'multiuser-review-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Multiuser observation workflow failed.' }
        python -B scripts/test_addin_selection_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'addin-selection-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Offline Add-In selection workflow failed.' }
        $result.addInSelectionMcp = 'passed; supplied selections, no native companion'
        python -B scripts/test_addin_receiver_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'addin-receiver-host')
        if ($LASTEXITCODE -ne 0) { throw 'Production Add-In receiver enrollment gate failed.' }
        $result.addInReceiverHost = 'passed; production enrollment unavailable, no worker launched'
        python -B scripts/test_encoder_checkpoint.py --fixture (Join-Path $output 'encoder-checkpoint/fixture.json')
        if ($LASTEXITCODE -ne 0) { throw 'C# encoder checkpoint receipt failed independent Python verification.' }
        $result.encoderCheckpointReceipt = 'passed'
        python -B scripts/test_translation_host_mcp.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'translation-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Offline translation MCP workflow failed.' }
        $result.translationMcp = 'passed'
        python -B scripts/test_translation_host_admission.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'translation-admission-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Translation native admission and qualification boundary failed.' }
        $result.translationAdmissionMcp = 'passed'
        $testSuiteFixture = Join-Path $output 'testsuite-production/host-fixture.json'
        python -B scripts/test_testsuite_style_contracts.py --fixture $testSuiteFixture --report (Join-Path $output 'testsuite-schema.json')
        if ($LASTEXITCODE -ne 0) { throw 'Test Suite JSON Schema harness failed.' }
        $result.testSuiteSchemas = 'passed'
        python -B scripts/test_testsuite_production_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture $testSuiteFixture --report (Join-Path $output 'testsuite-mcp.json')
        if ($LASTEXITCODE -ne 0) { throw 'Test Suite actual offline MCP workflow failed.' }
        $result.testSuiteMcp = 'passed'
        python -B scripts/test_testsuite_result_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'testsuite-intake/mcp-fixture.json') --output (Join-Path $output 'testsuite-result-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Supplied Test Suite result MCP workflow failed.' }
        $result.testSuiteResultMcp = 'passed'
        python -B scripts/test_software_unit_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'software-units/mcp-fixture.json') --output (Join-Path $output 'software-unit-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Software-unit MCP workflow failed.' }
        $result.softwareUnitMcp = 'passed'
        python -B scripts/test_connection_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'connections/mcp-fixture.json') --output (Join-Path $output 'connection-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Connection MCP workflow failed.' }
        $result.connectionMcp = 'passed'
        python -B scripts/test_composite_engineering_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'composite-engineering/mcp-fixture.json') --output (Join-Path $output 'composite-engineering-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Composite engineering MCP workflow failed.' }
        $result.compositeEngineeringMcp = 'passed'
        python -B scripts/test_composite_engineering_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'composite-engineering/consumer-v2/mcp-fixture.json') --output (Join-Path $output 'composite-consumers-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Composite consumer context MCP workflow failed.' }
        $result.compositeConsumersMcp = 'passed'
        python -B scripts/test_composite_engineering_compatibility.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'composite-prior-v1-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Actual prior composite v1 compatibility failed.' }
        $result.compositePriorCompatibility = 'passed'
        python -B scripts/test_composite_typed_contracts.py --fixture (Join-Path $output 'composite-engineering/typed-v3')
        if ($LASTEXITCODE -ne 0) { throw 'Independent typed composite verification failed.' }
        python -B scripts/test_composite_engineering_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'composite-engineering/typed-v3/mcp-fixture.json') --output (Join-Path $output 'composite-typed-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Composite typed mapping MCP workflow failed.' }
        python -B scripts/test_composite_engineering_compatibility.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture fixtures/composite-engineering/retained-v1-v2 --output (Join-Path $output 'composite-prior-v1-v2-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Actual prior composite v1/v2 compatibility failed.' }
        $result.compositeTypedAndPriorCompatibility = 'passed'
        python -B scripts/test_hmi_component_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'hmi-components/mcp-fixture.json') --output (Join-Path $output 'hmi-component-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Component/HMI MCP workflow failed.' }
        python -B scripts/test_hmi_text_list_generation_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'hmi-components/text-generation/mcp-fixture.json') --output (Join-Path $output 'hmi-text-generation-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Bound HMI text-list generation MCP workflow failed.' }
        python -B scripts/test_hmi_numeric_generation_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'hmi-components/text-numeric/mcp-fixture.json') --output (Join-Path $output 'hmi-numeric-generation-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Numeric HMI state generation MCP workflow failed.' }
        python -B scripts/test_hmi_coordinated_generation_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'hmi-components/text-coordinated/mcp-fixture.json') --output (Join-Path $output 'hmi-coordinated-generation-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Coordinated HMI generation-set MCP workflow failed.' }
        $result.hmiCoordinatedGeneration = 'passed; exact declared output/binding set and original dependency/consumer limits'
        python -B scripts/qualify_hmi_coordinated_prior.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'hmi-coordinated-prior')
        if ($LASTEXITCODE -ne 0) { throw 'Actual prior text-list author compatibility failed.' }
        $result.hmiAuthorPriorCompatibility = 'passed; retained actual v1/v2/v3 producer bytes'
        python -B scripts/test_hmi_set_evolution_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'hmi-components/text-evolution/mcp-fixture.json') --output (Join-Path $output 'hmi-set-evolution-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Explicit HMI set evolution and conflict workflow failed.' }
        python -B scripts/qualify_hmi_set_evolution_prior.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'hmi-set-evolution-prior')
        if ($LASTEXITCODE -ne 0) { throw 'Actual prior HMI generation-set compatibility failed.' }
        $result.hmiSetEvolution = 'passed; explicit membership, identity and binding changes with retained v1 set compatibility'
        python -B scripts/test_hmi_integer_conversion_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'hmi-components/text-conversion/mcp-fixture.json') --output (Join-Path $output 'hmi-integer-conversion-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Explicit integer text-list conversion workflow failed.' }
        $result.hmiIntegerConversion = 'passed; explicit checked listed-value conversion, overflow/manual-edit refusals and retained lineage'
        $result.hmiTextListGeneration = 'passed; exact supplied component binding and retained candidates'
        $result.hmiComponentMcp = 'passed'
        python -B scripts/hmi_derived_effect_checks.py --fixture (Join-Path $output 'hmi-component-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Independent HMI expected-effect checks failed.' }
        python -B scripts/test_hmi_component_compatibility.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'hmi-component-prior-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Actual prior HMI record compatibility failed.' }
        $result.hmiDerivedEffectsAndPrior = 'passed'
        python -B scripts/test_db_snapshot_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'db-snapshots/mcp-fixture.json') --output (Join-Path $output 'db-snapshot-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'DB snapshot MCP workflow failed.' }
        $result.dbSnapshotMcp = 'passed'
        python -B scripts/test_safety_evidence_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'safety-evidence/mcp-fixture.json') --output (Join-Path $output 'safety-evidence-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Safety evidence MCP workflow failed.' }
        $result.safetyEvidenceMcp = 'passed'
        python -B scripts/test_manifest_publication_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'manifest-publication-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Manifest publication MCP recovery workflow failed.' }
        $result.manifestPublicationMcp = 'passed'
        python -B scripts/test_drive_profile_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'drive-profiles/mcp-fixture.json') --output (Join-Path $output 'drive-profile-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Drive profile MCP workflow failed.' }
        $result.driveProfileMcp = 'passed'
        python -B scripts/test_drive_telegram_contracts.py --fixture (Join-Path $output 'drive-profiles/telegrams-v2')
        if ($LASTEXITCODE -ne 0) { throw 'Typed telegram independent input/schema checks failed.' }
        python -B scripts/test_drive_profile_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'drive-profiles/telegrams-v2/mcp-fixture.json') --output (Join-Path $output 'drive-telegrams-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Typed telegram MCP workflow failed.' }
        $result.driveTelegramsMcp = 'passed'
        python -B scripts/test_technology_profile_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'technology-profiles/mcp-fixture.json') --output (Join-Path $output 'technology-profile-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Technology profile MCP workflow failed.' }
        $result.technologyProfileMcp = 'passed'
        python -B scripts/test_technology_encoder_contracts.py --fixture (Join-Path $output 'technology-profiles/encoders-v4')
        if ($LASTEXITCODE -ne 0) { throw 'Typed encoder independent source/schema checks failed.' }
        python -B scripts/test_technology_profile_compatibility.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'technology-prior-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Prior technology record compatibility failed.' }
        $result.technologyPriorMcp = 'passed'
        python -B scripts/test_access_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'access/mcp-fixture.json') --output (Join-Path $output 'access-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Public access MCP workflow failed.' }
        $result.accessMcp = 'passed'
        python -B scripts/test_runtime_handoff_contracts.py --fixture (Join-Path $output 'access/handoff')
        if ($LASTEXITCODE -ne 0) { throw 'Security/runtime handoff original-evidence checks failed.' }
        python -B scripts/test_access_compatibility.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'access-prior-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Prior access record compatibility failed.' }
        $result.runtimeHandoffOriginals = 'passed'
        $result.accessPriorMcp = 'passed'
        python -B scripts/test_runtime_handoff_workflow.py --fixture (Join-Path $output 'access/handoff-workflow')
        if ($LASTEXITCODE -ne 0) { throw 'Retained handoff original-evidence checks failed.' }
        python -B scripts/test_runtime_handoff_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'access/handoff-workflow/mcp-fixture.json') --output (Join-Path $output 'runtime-handoff-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Security/runtime handoff MCP workflow failed.' }
        $result.runtimeHandoffMcp = 'passed'
        python -B scripts/test_public_certificate_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'public-certificates/mcp-fixture.json') --output (Join-Path $output 'public-certificate-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Public certificate MCP workflow failed.' }
        $result.publicCertificateMcp = 'passed'
        python -B scripts/test_web_application_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'web-applications/mcp-fixture.json') --output (Join-Path $output 'web-application-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Web application MCP workflow failed.' }
        $result.webApplicationMcp = 'passed'
        python -B scripts/test_external_resource_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'external-resources/mcp-fixture.json') --output (Join-Path $output 'external-resource-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'External resource MCP workflow failed.' }
        $result.externalResourceMcp = 'passed'
        python -B scripts/test_credential_cli.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'credential-cli')
        if ($LASTEXITCODE -ne 0) { throw 'Protected credential local CLI failed.' }
        $result.credentialCli = 'passed'
        python -B scripts/test_credential_operation_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'credentials/operations/mcp-fixture.json') --output (Join-Path $output 'credential-operation-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Credential operation status checks failed.' }
        $result.credentialOperations = 'passed'
        python -B scripts/test_credential_operation_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --fixture (Join-Path $output 'credentials/recovery/mcp-fixture.json') --output (Join-Path $output 'credential-recovery-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Credential recovery status checks failed.' }
        $result.credentialRecovery = 'passed'


        python -B scripts/verify_feature_catalog.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --report (Join-Path $output 'catalog-mcp.json')
        if ($LASTEXITCODE -ne 0) { throw 'Actual Host feature catalog checks failed.' }
        $result.catalogMcp = 'passed'
        python -B scripts/test_watch_qualification_host.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe")
        if ($LASTEXITCODE -ne 0) { throw 'Watch qualification registration boundary failed.' }
        $result.watchQualificationBoundary = 'passed'
        python -B scripts/test_opcua_host_mcp.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'opcua-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'OPC UA actual offline MCP workflow failed.' }
        $result.opcUaMcp = 'passed'
        python -B scripts/test_vci_host_mcp.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --git-executable $gitExecutable --output (Join-Path $output 'vci-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'VCI actual offline MCP workflow failed.' }
        $result.vciMcp = 'passed'
        python -B scripts/test_graph_host_mcp.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'graph-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'GRAPH actual offline MCP workflow failed.' }
        $result.graphMcp = 'passed'
        python -B scripts/test_automationml_host_mcp.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --output (Join-Path $output 'automationml-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Offline AutomationML MCP workflow failed.' }
        $result.automationmlMcp = 'passed'
        python -B scripts/qualify_trace_diagnostics_offline.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --samples (Join-Path $output 'diagnostic-samples.json') --output (Join-Path $output 'diagnostic-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Offline review intake and diagnostic MCP workflow failed.' }
        $result.diagnosticMcp = 'passed'
        python -B scripts/qualify_diagnostic_experiments_offline.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --samples (Join-Path $output 'diagnostic-samples.json') --output (Join-Path $output 'diagnostic-experiment-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Offline diagnostic experiment composition and before/after workflow failed.' }
        $result.diagnosticExperiments = 'passed; original source, instance, Test Suite/alarm and measurement lineage'
        python -B scripts/qualify_repeated_diagnostic_experiments_offline.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --samples (Join-Path $output 'diagnostic-samples.json') --output (Join-Path $output 'diagnostic-repeat-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Repeated diagnostic experiments and combined report export failed.' }
        $result.diagnosticRepeatedExperiments = 'passed; repeated windows, quality and original evidence rederivation'
        python -B scripts/qualify_runtime_diagnostic_acquisition_offline.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --samples (Join-Path $output 'diagnostic-samples.json') --output (Join-Path $output 'runtime-diagnostic-acquisition-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Retained runtime acquisition and diagnostic report workflow failed.' }
        $result.runtimeDiagnosticAcquisition = 'passed; exact immutable capture sources and unresolved sequential observations'
        python -B scripts/qualify_diagnostic_quality_offline.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --samples (Join-Path $output 'diagnostic-samples.json') --output (Join-Path $output 'diagnostic-quality-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Diagnostic sample quality, clock epochs and qualified report workflow failed.' }
        $result.diagnosticQuality = 'passed; quality-aware measurement, comparison and repeated experiment reports'
        python -B scripts/qualify_diagnostic_ua_binding.py --host (Join-Path $buildOutput "bin/TiaMcp.Host/$($Configuration.ToLowerInvariant())/TiaMcp.Host.exe") --server (Join-Path $buildOutput "bin/TiaMcp.OpcUa.Runtime.Tests/$($Configuration.ToLowerInvariant())/TiaMcp.OpcUa.Runtime.Tests.dll") --samples (Join-Path $output 'diagnostic-samples.json') --output (Join-Path $output 'diagnostic-ua-binding-mcp')
        if ($LASTEXITCODE -ne 0) { throw 'Prebound UA diagnostic intent and capture restart workflow failed.' }
        $result.diagnosticUaBinding = 'passed; pre-acquisition intent, retained observations, adverse quality/sequence evidence, readable exports and restart without replay'

    }
    $result.status = 'passed'
    Write-Output "PASS Offline validation ($Suite). No Siemens/native or runtime qualification is claimed."
} catch {
    $result.status = 'failed'
    $result.error = $_.Exception.Message
    throw
} finally {
    $result.completedAtUtc = [DateTime]::UtcNow.ToString('O')
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8
    Stop-Transcript -ErrorAction SilentlyContinue | Out-Null
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    Pop-Location
}
