param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $repoRoot ('artifacts/opcua-offline/' + [Guid]::NewGuid().ToString('N'))
$snapshot = Join-Path $runRoot 'source'
New-Item -ItemType Directory -Path $snapshot -Force | Out-Null
$result = @{schemaVersion=1;status='running';nativeQualification=$false;targetProbed=$false;startedAtUtc=[DateTime]::UtcNow.ToString('O');checks=@{}}
$files = @()
foreach ($folder in @('src/TiaMcp.Core','src/TiaMcp.Graph','src/TiaMcp.OpcUa','src/TiaMcp.OpcUa.Cli','tests/TiaMcp.OpcUa.Tests','tests/TiaMcp.OpcUa.Dependencies.Tests','contracts','fixtures/opcua','third-party/opcua')) {
    $files += @(Get-ChildItem -LiteralPath (Join-Path $repoRoot $folder) -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|__pycache__)[\\/]' })
}
foreach ($file in @('Directory.Build.props','global.json','NuGet.Config','scripts/probe_opcua_api.py','scripts/Inspect-OpcUaApi.ps1','scripts/test_opcua_api.py','scripts/Test-OpcUaOffline.ps1')) {
    $files += Get-Item -LiteralPath (Join-Path $repoRoot $file)
}
# Core can embed fixture resources and link source files outside its source directory. Include
# its declared files without making this runner depend on any unrelated optional fixture by name.
$coreProjectPath = Join-Path $repoRoot 'src/TiaMcp.Core/TiaMcp.Core.csproj'
$coreProject = [xml](Get-Content -LiteralPath $coreProjectPath -Raw)
foreach ($resource in $coreProject.SelectNodes('//EmbeddedResource[@Include] | //Compile[@Include]')) {
    $resourcePath = Join-Path (Split-Path $coreProjectPath -Parent) $resource.GetAttribute('Include')
    if (Test-Path -LiteralPath $resourcePath -PathType Leaf) { $files += Get-Item -LiteralPath $resourcePath }
}
$files = @($files | Sort-Object FullName -Unique)
try {
    $records = @()
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($repoRoot, $file.FullName)
        $before = (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        $target = Join-Path $snapshot $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
        if ((Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant() -ne $before) { throw "Source changed during snapshot: $relative" }
        $records += @{path=$relative;sha256=$before}
    }
    foreach ($record in $records) {
        if ((Get-FileHash -LiteralPath (Join-Path $repoRoot $record.path)).Hash.ToLowerInvariant() -ne $record.sha256) { throw "Source changed during snapshot: $($record.path)" }
    }
    @{schemaVersion=1;files=$records} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'source-manifest.json') -Encoding utf8
    $result.sourceManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $runRoot 'source-manifest.json')).Hash.ToLowerInvariant()
    $result.dotnetSdk = (dotnet --version)
    $result.python = (python --version)
    Push-Location $snapshot
    try {
        python -B -m unittest discover -s scripts -p test_opcua_api.py -v *> (Join-Path $runRoot 'python.log')
        if ($LASTEXITCODE -ne 0) { throw 'OPC UA metadata tests failed.' }
        $result.checks.metadataTests = 'passed'
        $packages = Join-Path $repoRoot '.packages'
        dotnet build tests/TiaMcp.OpcUa.Tests/TiaMcp.OpcUa.Tests.csproj -c $Configuration -m:1 -nr:false "-p:RestorePackagesPath=$packages" --ignore-failed-sources *> (Join-Path $runRoot 'build-tests.log')
        if ($LASTEXITCODE -ne 0) { throw 'OPC UA test build failed.' }
        $testDll = Join-Path $snapshot "tests/TiaMcp.OpcUa.Tests/bin/$Configuration/net10.0/TiaMcp.OpcUa.Tests.dll"
        dotnet $testDll *> (Join-Path $runRoot 'managed.log')
        if ($LASTEXITCODE -ne 0) { throw 'OPC UA managed tests failed.' }
        $result.checks.managedTests = 'passed'
        dotnet build src/TiaMcp.OpcUa.Cli/TiaMcp.OpcUa.Cli.csproj -c $Configuration -m:1 -nr:false "-p:RestorePackagesPath=$packages" --ignore-failed-sources *> (Join-Path $runRoot 'build-cli.log')
        if ($LASTEXITCODE -ne 0) { throw 'OPC UA CLI build failed.' }
        $cli = Join-Path $snapshot "src/TiaMcp.OpcUa.Cli/bin/$Configuration/net10.0/TiaMcp.OpcUa.Cli.dll"
        dotnet build tests/TiaMcp.OpcUa.Dependencies.Tests/TiaMcp.OpcUa.Dependencies.Tests.csproj -c $Configuration -m:1 -nr:false "-p:RestorePackagesPath=$packages" --ignore-failed-sources *> (Join-Path $runRoot 'build-dependencies.log')
        if ($LASTEXITCODE -ne 0) { throw 'OPC UA dependency test build failed.' }
        $dependencyTestDll = Join-Path $snapshot "tests/TiaMcp.OpcUa.Dependencies.Tests/bin/$Configuration/net10.0/TiaMcp.OpcUa.Dependencies.Tests.dll"
        dotnet $dependencyTestDll $cli *> (Join-Path $runRoot 'dependencies.log')
        if ($LASTEXITCODE -ne 0) { throw 'OPC UA dependency and CLI tests failed.' }
        $result.checks.dependencyTests = 'passed'
        dotnet $cli inspect fixtures/opcua/machine-v1.xml (Join-Path $runRoot 'machine-v1-review.json') *> (Join-Path $runRoot 'cli.log')
        if ($LASTEXITCODE -ne 0) { throw 'CLI model inspection failed.' }
        dotnet $cli compare fixtures/opcua/machine-v1.xml fixtures/opcua/machine-v2.xml (Join-Path $runRoot 'machine-breaking-impact.json') fixtures/opcua/consumers-v1.json *>> (Join-Path $runRoot 'cli.log')
        if ($LASTEXITCODE -ne 0) { throw 'CLI breaking model review failed.' }
        $impact = Get-Content -LiteralPath (Join-Path $runRoot 'machine-breaking-impact.json') -Raw | ConvertFrom-Json
        if ($impact.breakingChangeCount -ne 3 -or @($impact.clientImpacts | Where-Object status -eq 'affected').Count -ne 3 -or $impact.compatibilityCertified) { throw 'Independent fixture impact expectations failed.' }
        $result.checks.fixtureImpact = 'passed'
        # Existing outputs are immutable even if the caller accidentally reuses a report path.
        $reportHash = (Get-FileHash -LiteralPath (Join-Path $runRoot 'machine-v1-review.json')).Hash
        dotnet $cli inspect fixtures/opcua/machine-v2.xml (Join-Path $runRoot 'machine-v1-review.json') *>> (Join-Path $runRoot 'cli.log')
        if ($LASTEXITCODE -eq 0 -or (Get-FileHash -LiteralPath (Join-Path $runRoot 'machine-v1-review.json')).Hash -ne $reportHash) { throw 'CLI overwrite protection failed.' }
        $result.checks.existingOutputPreservation = 'passed'
        $result.binaries = @(Get-ChildItem -LiteralPath (Split-Path $cli -Parent) -File | Where-Object { $_.Extension -in '.dll','.exe','.json' } | ForEach-Object {
            @{path=[IO.Path]::GetRelativePath($runRoot,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
        })
        $result.testBinary = @{path=[IO.Path]::GetRelativePath($runRoot,$testDll);sha256=(Get-FileHash -LiteralPath $testDll).Hash.ToLowerInvariant()}
        $result.dependencyTestBinary = @{path=[IO.Path]::GetRelativePath($runRoot,$dependencyTestDll);sha256=(Get-FileHash -LiteralPath $dependencyTestDll).Hash.ToLowerInvariant()}
        $result.checks.nativeAcceptance = 'notRun'
        $result.status = 'passed'
    } finally { Pop-Location }
} catch {
    $result.status = 'failed'; $result.error = $_.Exception.Message
    throw
} finally {
    $result.completedAtUtc = [DateTime]::UtcNow.ToString('O')
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8
    Write-Output $runRoot
}
# The final native CLI command intentionally fails to prove overwrite refusal.
# A successful runner must not leak that expected exit code to its caller/CI.
$global:LASTEXITCODE = 0
