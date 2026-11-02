#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$BundlePath,
    [string]$ReportPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$script = Join-Path $PSScriptRoot 'Register-TiaOpennessApplication.ps1'
$synthetic = -not $BundlePath
if ($synthetic) {
    # Manifest-consistent fixture only: native binaries are inert text, never executed.
    $BundlePath = Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/registration-fixture-' + [Guid]::NewGuid().ToString('N'))
    $required = @('host/TiaMcp.Host.exe','host/TiaMcp.Host.dll','host/TiaMcp.Host.deps.json','host/TiaMcp.Host.runtimeconfig.json',
        'workers/openness/TiaMcp.Openness.V21.exe','workers/simulator/TiaMcp.Simulator.V8.exe','scripts/Start-TiaMcp.ps1','scripts/Verify-TiaMcpBundle.ps1',
        'ui/project-explorer/index.html','ui/project-explorer/index.template.html','ui/project-explorer/model.js','ui/project-explorer/client.js','ui/project-explorer/explorer.js','ui/project-explorer/style.css',
        'contracts/project-model.v1.schema.json','contracts/project-model-provider.v1.schema.json','contracts/typed-graphical-model.schema.json','contracts/typed-graphical-change.schema.json',
        'contracts/typed-graphical-read.schema.json','contracts/typed-graphical-profiles.schema.json','fixtures/typed-graphical/profiles.v1.json','fixtures/typed-graphical/instance-effects.v1.json')
    $files = @($required | ForEach-Object {
        $file = Join-Path $BundlePath $_
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file))
        [IO.File]::WriteAllText($file,'Inert registration inspection fixture; never execute.')
        @{path=$_;bytes=(Get-Item -LiteralPath $file).Length;sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    @{schemaVersion=1;bundleKind='engineering';productVersion=$null;files=$files} | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $BundlePath 'manifest.json') -Encoding UTF8
}
$bundle = (Get-Item -LiteralPath $BundlePath -Force).FullName
$worker = Join-Path $bundle 'workers/openness/TiaMcp.Openness.V21.exe'
$expectedHash = (Get-FileHash -LiteralPath $worker -Algorithm SHA256).Hash.ToLowerInvariant()
$results = New-Object 'System.Collections.Generic.List[object]'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('tia-registration-inspection-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($tempRoot)
$savedPath = Join-Path $tempRoot 'proposal.json'
$invalidBundle = Join-Path $tempRoot 'invalid-bundle'
[void][IO.Directory]::CreateDirectory($invalidBundle)
$applicationPath = 'SOFTWARE\Siemens\Automation\Openness\AllowList\TiaMcp.Openness.V21.exe'
. (Join-Path $PSScriptRoot 'OpennessRegistration.Helpers.ps1')
function Snapshot-Registry {
    $base = $null; $entry = $null
    try {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
        $entry = $base.OpenSubKey($applicationPath,$false)
        if ($null -eq $entry) { return '{"existed":false}' }
        $values = @($entry.GetSubKeyNames() | Sort-Object | ForEach-Object {
            [pscustomobject]@{name=$_;entry=(Read-OpennessEntry $base ($applicationPath + '\' + $_))}
        })
        return ([ordered]@{existed=$true;values=$values} | ConvertTo-Json -Depth 8 -Compress)
    }
    catch { return '{"status":"unavailable"}' }
    finally { if ($entry) {$entry.Dispose()}; if ($base) {$base.Dispose()} }
}
function Test-Case([string]$Name,[scriptblock]$Body) {
    & $Body
    $results.Add([pscustomobject]@{name=$Name;status='passed'})
    Write-Output ('PASS ' + $Name)
}
function Require-Rejection([scriptblock]$Body) {
    $rejected=$false
    try { & $Body | Out-Null } catch { $rejected=$true }
    if (-not $rejected) { throw 'Unsafe registration inspection request was not rejected.' }
}
$before=Snapshot-Registry
try {
    Test-Case 'Verified bundle inspection proposes exact worker and official V21 key' {
        $inspection = & $script -BundlePath $bundle -ExpectedWorkerSha256 $expectedHash -InspectOnly | ConvertFrom-Json
        if ($inspection.status -ne 'inspected' -or ($inspection.registryEntry -and -not $inspection.registryEntry.StartsWith($applicationPath + '\',[StringComparison]::Ordinal)) -or $inspection.registryView -ne 'Registry64' -or
            $inspection.proposedValues.Path -cne $worker -or -not $inspection.strictBundleVerification.verified -or -not $inspection.strictBundleVerification.strict) { throw 'Proposal identity or verification is wrong.' }
        if ($inspection.registryWritesAttempted -or $inspection.registrationWritten -or $inspection.readBackVerified) { throw 'InspectOnly declared a registry write.' }
    }
    Test-Case 'UTC millisecond format is invariant and SHA256 is Base64 raw bytes' {
        $inspection = & $script -BundlePath $bundle -ExpectedWorkerSha256 $expectedHash -InspectOnly | ConvertFrom-Json
        $date=(Get-Item -LiteralPath $worker).LastWriteTimeUtc.ToString('yyyy/MM/dd HH:mm:ss.fff',[Globalization.CultureInfo]::InvariantCulture)
        if ($inspection.proposedValues.DateModified -cne $date -or $date -cnotmatch '^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\.\d{3}$') { throw 'UTC timestamp format differs.' }
        $digestHex=[BitConverter]::ToString([Convert]::FromBase64String($inspection.proposedValues.FileHash)).Replace('-','').ToLowerInvariant()
        if ($digestHex -cne $expectedHash) { throw 'FileHash is not the approved raw SHA256 represented as Base64.' }
    }
    Test-Case 'Wrong expected worker hash rejects before registry access/write' {
        Require-Rejection { & $script -BundlePath $bundle -ExpectedWorkerSha256 ('0' * 64) -InspectOnly }
    }
    Test-Case 'Relative, traversal, stream and UNC bundle paths rejected' {
        foreach ($bad in @('relative/path','C:relative','\\server\bundle','\\?\C:\bundle','C:\folder\..\bundle','C:\bundle:stream','C:\bundle.')) {
            Require-Rejection { & $script -BundlePath $bad -ExpectedWorkerSha256 $expectedHash -InspectOnly }
        }
    }
    Test-Case 'An existing folder without a strict bundle manifest is rejected' {
        Require-Rejection { & $script -BundlePath $invalidBundle -ExpectedWorkerSha256 $expectedHash -InspectOnly }
    }
    Test-Case 'Report must remain outside immutable bundle and cannot overwrite earlier evidence' {
        Require-Rejection { & $script -BundlePath $bundle -ExpectedWorkerSha256 $expectedHash -ReportPath (Join-Path $bundle 'registration.json') -InspectOnly }
        $inspection = & $script -BundlePath $bundle -ExpectedWorkerSha256 $expectedHash -ReportPath $savedPath -InspectOnly | ConvertFrom-Json
        $saved = Get-Content -LiteralPath $savedPath -Raw | ConvertFrom-Json
        if ($saved.proposedValues.FileHash -cne $inspection.proposedValues.FileHash -or $saved.previousEntry.status -ne $inspection.previousEntry.status) { throw 'Saved recovery/proposal evidence differs.' }
        Require-Rejection { & $script -BundlePath $bundle -ExpectedWorkerSha256 $expectedHash -ReportPath $savedPath -InspectOnly }
    }
    Test-Case 'All inspections and rejected requests leave existing entry byte-for-byte equivalent' {
        if ((Snapshot-Registry) -cne $before) { throw 'Registration inspection changed the host AllowList entry.' }
    }
    Test-Case 'Setup inspection generates final-path configuration without creating files or registering' {
        $configPath = Join-Path $tempRoot 'client.json'
        $setupReport = Join-Path $tempRoot 'setup-report.json'
        $setup = & (Join-Path $PSScriptRoot 'Setup-TiaMcp.ps1') -BundlePath $bundle -ExpectedWorkerSha256 $expectedHash -InspectOnly -ArtifactsDirectory (Join-Path $tempRoot 'artifacts') -ConfigurationPath $configPath -ReportPath $setupReport | ConvertFrom-Json
        if ($setup.status -ne 'inspected' -or $setup.configurationWritten -or (Test-Path -LiteralPath $configPath) -or (Test-Path -LiteralPath $setupReport) -or
            $setup.configuration.mcpServers.'tia-v21'.args -notcontains ($bundle + '\scripts\Start-TiaMcp.ps1') -or (Snapshot-Registry) -cne $before) { throw 'Setup inspection changed state or generated the wrong final path.' }
        Require-Rejection { & (Join-Path $PSScriptRoot 'Setup-TiaMcp.ps1') -BundlePath $bundle -ExpectedWorkerSha256 $expectedHash -InspectOnly -ArtifactsDirectory (Join-Path $bundle 'artifacts') -ConfigurationPath $configPath }
        Require-Rejection { & (Join-Path $PSScriptRoot 'Setup-TiaMcp.ps1') -BundlePath $bundle -ExpectedWorkerSha256 $expectedHash -InspectOnly -ArtifactsDirectory (Join-Path $tempRoot 'artifacts') -ConfigurationPath $configPath -ReportPath $configPath }
    }
    $report = [ordered]@{schemaVersion=1;capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');status='passed';testsPassed=$results.Count;tests=$results.ToArray();workerSha256=$expectedHash;bundlePath=$bundle;syntheticBundle=$synthetic;registryWritesPerformed=$false;applyModeTested=$false;registryComparisonAvailable=($before -ne '{"status":"unavailable"}')}
    if ($ReportPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath),($report | ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false))) }
    Write-Output ('PASS ' + $results.Count + ' inspect-only registration guard tests; no apply invocation.')
}
finally {
    if ([IO.File]::Exists($savedPath)) { [IO.File]::Delete($savedPath) }
    [IO.Directory]::Delete($invalidBundle,$false)
    [IO.Directory]::Delete($tempRoot,$false)
}
