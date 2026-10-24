#Requires -Version 5.1
[CmdletBinding()]
param([string]$ReportPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$doctor = Join-Path $PSScriptRoot 'Test-TiaMcpEnvironment.ps1'
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('tia-mcp-doctor-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testDirectory)
$fixturePath = Join-Path $testDirectory 'evidence.json'
$results = New-Object 'System.Collections.Generic.List[object]'

function New-Evidence {
    $assemblies = @('Siemens.Engineering.Base','Siemens.Engineering.Step7','Siemens.Engineering.Safety') | ForEach-Object {
        @{binary=@{status='present';path=('C:\TIA\' + $_ + '.dll');assembly=@{status='present';name=$_;version='21.0.0.0'}}}
    }
    return (@{
        schemaVersion=1
        host=@{
            is64BitOperatingSystem=$true
            dotnetExecutable=@{status='present';architecture='x64'}
            runtimeFamilies=@(
                @{name='Microsoft.NETCore.App';status='read';versions=@(@{version='10.0.12';marker=@{status='present'}})},
                @{name='Microsoft.AspNetCore.App';status='read';versions=@(@{version='10.0.12';marker=@{status='present'}})}
            )
            package=@{status='not_requested';files=@()}
        }
        probe=@{
            framework=@{runtime=@{status='read';release=533509;supportsNet48=$true}}
            tiaV21=@{enumerationStatus='read';assemblies=@($assemblies)}
            currentToken=@{groups=@(
                @{name='Siemens TIA Openness';status='read';presentInCurrentToken=$true;authorizedInCurrentToken=$true},
                @{name='PlcSimUsers';status='read';presentInCurrentToken=$true;authorizedInCurrentToken=$true}
            )}
            simulators=@{advanced=@{
                apiEnumeration=@{status='read'}
                runtimeManager=@{status='present'}
                apiVersions=@(@{versionDirectory='8.0';x64=@{status='present';assembly=@{status='present';name='Siemens.Simatic.Simulation.Runtime.Api.x64';version='8.0.0.1'}}})
            }}
        }
    } | ConvertTo-Json -Depth 20 | ConvertFrom-Json)
}

function Run-Evidence {
    param($Evidence, [switch]$RequireSimulation)
    [IO.File]::WriteAllText($fixturePath, ($Evidence | ConvertTo-Json -Depth 20))
    $report = (& $doctor -EvidencePath $fixturePath -RequireSimulation:$RequireSimulation) | ConvertFrom-Json
    if ($report.mode -ne 'supplied-evidence' -or $report.settingsModified -or $report.nativeWorkersStarted -or $report.licensesInspected) { throw 'Evidence-only safety flags invalid.' }
    return $report
}

function Add-PackageEvidence {
    param($Evidence)
    $Evidence.host.package = ([pscustomobject]@{
        status='read'
        files=@(@('host/TiaMcp.Host.exe','host/TiaMcp.Host.dll','host/TiaMcp.Host.deps.json','host/TiaMcp.Host.runtimeconfig.json',
            'workers/openness/TiaMcp.Openness.V21.exe','workers/simulator/TiaMcp.Simulator.V8.exe') | ForEach-Object {
            [pscustomobject]@{relativePath=$_;metadata=[pscustomobject]@{status='present';architecture='x64'}}
        })
        runtimeConfigStatus='read'
        runtimeConfig=[pscustomobject]@{runtimeOptions=[pscustomobject]@{frameworks=@(
            [pscustomobject]@{name='Microsoft.NETCore.App';version='10.0.0'},[pscustomobject]@{name='Microsoft.AspNetCore.App';version='10.0.0'}
        )}}
    })
}

function Assert-Check {
    param($Report,[string]$Id,[string]$Expected)
    $matching = @($Report.checks | Where-Object { $_.id -eq $Id })
    if ($matching.Count -ne 1 -or $matching[0].status -ne $Expected) { throw "Expected $Id=$Expected." }
}

function Run-Test {
    param([string]$Name,[scriptblock]$Body)
    & $Body
    $results.Add([pscustomobject]@{name=$Name;status='passed'})
    Write-Output "PASS $Name"
}

try {
    $tokens = $null
    $parseErrors = $null
    $probeAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Probe-Environment.ps1'), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw 'Probe script did not parse.' }
    $resolver = @($probeAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-TiaApiDirectory' }, $true))
    if ($resolver.Count -ne 1) { throw 'Expected one production TIA API directory resolver.' }
    . ([scriptblock]::Create($resolver[0].Extent.Text))

    Run-Test 'Explicit TIA API directory wins without reading registry' {
        function Get-RegisteredTiaApiDirectory { throw 'Registry must not be read.' }
        $actual = Get-TiaApiDirectory 'D:\explicit\net48' 'C:\default\net48'
        if ($actual -ne 'D:\explicit\net48') { throw 'Explicit directory was ignored.' }
    }
    Run-Test 'Registered TIA API directory wins over setup default' {
        function Get-RegisteredTiaApiDirectory { return 'D:\registered\net48' }
        $actual = Get-TiaApiDirectory '' 'C:\default\net48'
        if ($actual -ne 'D:\registered\net48') { throw 'Registered directory was ignored.' }
    }
    Run-Test 'Absent TIA API registration uses setup default' {
        function Get-RegisteredTiaApiDirectory { return $null }
        $actual = Get-TiaApiDirectory '' 'C:\default\net48'
        if ($actual -ne 'C:\default\net48') { throw 'Setup default was not used.' }
    }
    Run-Test 'Denied TIA API registration does not fall back to a missing path' {
        function Get-RegisteredTiaApiDirectory { throw [UnauthorizedAccessException]::new('Registry denied.') }
        $rejected = $false
        try { Get-TiaApiDirectory '' 'C:\default\net48' | Out-Null }
        catch [UnauthorizedAccessException] { $rejected = $true }
        if (-not $rejected) { throw 'Registry failure silently selected a fallback.' }
    }
    Run-Test 'Supplied evidence never invokes the local probe' {
        $isolatedDoctor = Join-Path $testDirectory 'Test-TiaMcpEnvironment.ps1'
        [IO.File]::Copy($doctor, $isolatedDoctor)
        [IO.File]::WriteAllText((Join-Path $testDirectory 'Probe-Environment.ps1'), "throw 'Probe must not run in supplied-evidence mode.'")
        [IO.File]::WriteAllText($fixturePath, ((New-Evidence) | ConvertTo-Json -Depth 20))
        $report = (& $isolatedDoctor -EvidencePath $fixturePath) | ConvertFrom-Json
        if ($report.mode -ne 'supplied-evidence' -or $report.selectedPrerequisites.status -ne 'present') { throw 'Evidence mode probed the machine or lost supplied evidence.' }
    }
    Run-Test 'Metadata present never becomes runtime or license qualification' {
        $report = Run-Evidence (New-Evidence)
        if ($report.selectedPrerequisites.status -ne 'present' -or $report.capabilities.simulation.qualification -ne 'unverified') { throw 'Qualification leaked from installation metadata.' }
        Assert-Check $report 'tia.attachment' 'unverified'
        Assert-Check $report 'simulation.execution' 'unverified'
    }
    Run-Test 'Missing ASP.NET shared framework blocks host despite NETCore runtime' {
        $e = New-Evidence
        $e.host.runtimeFamilies[1].versions = @()
        $report = Run-Evidence $e
        Assert-Check $report 'dotnet.Microsoft.AspNetCore.App' 'missing'
        if ($report.capabilities.host.status -ne 'blocked') { throw 'Host not blocked.' }
    }
    Run-Test 'A newer .NET major does not satisfy required runtime' {
        $e = New-Evidence
        $e.host.runtimeFamilies[0].versions[0].version = '11.0.1'
        Assert-Check (Run-Evidence $e) 'dotnet.Microsoft.NETCore.App' 'missing'
    }
    Run-Test 'Preview version does not satisfy stable framework' {
        $e = New-Evidence
        $e.host.runtimeFamilies[0].versions[0].version = '10.0.0-preview.4'
        Assert-Check (Run-Evidence $e) 'dotnet.Microsoft.NETCore.App' 'missing'
    }
    Run-Test 'Missing core runtime marker does not certify a directory' {
        $e = New-Evidence
        $e.host.runtimeFamilies[0].versions[0].marker.status = 'missing'
        Assert-Check (Run-Evidence $e) 'dotnet.Microsoft.NETCore.App' 'missing'
    }
    Run-Test 'Unreadable runtime marker is unavailable rather than missing' {
        $e = New-Evidence
        $e.host.runtimeFamilies[0].versions[0].marker.status = 'unavailable'
        $report = Run-Evidence $e
        Assert-Check $report 'dotnet.Microsoft.NETCore.App' 'unavailable'
        if ($report.capabilities.host.status -ne 'unknown') { throw 'Read restriction misclassified.' }
    }
    Run-Test 'Truncated runtime inventory does not establish absence' {
        $e = New-Evidence
        $e.host.runtimeFamilies[0].versions = @()
        $e.host.runtimeFamilies[0] | Add-Member -NotePropertyName truncated -NotePropertyValue $true
        Assert-Check (Run-Evidence $e) 'dotnet.Microsoft.NETCore.App' 'unavailable'
    }
    Run-Test 'Wrong .NET executable architecture blocks host' {
        $e = New-Evidence
        $e.host.dotnetExecutable.architecture = 'x86'
        Assert-Check (Run-Evidence $e) 'dotnet.x64' 'mismatch'
    }
    Run-Test 'Older .NET Framework does not satisfy worker runtime' {
        $e = New-Evidence
        $e.probe.framework.runtime.supportsNet48 = $false
        $e.probe.framework.runtime.release = 461808
        Assert-Check (Run-Evidence $e) 'framework.net48' 'mismatch'
    }
    Run-Test 'Wrong Openness assembly identity rejected' {
        $e = New-Evidence
        $e.probe.tiaV21.assemblies[0].binary.assembly.version = '20.0.0.0'
        Assert-Check (Run-Evidence $e) 'tia.Siemens.Engineering.Base' 'mismatch'
    }
    Run-Test 'Missing worker reference reported separately' {
        $e = New-Evidence
        $e.probe.tiaV21.assemblies = @($e.probe.tiaV21.assemblies | Select-Object -First 2)
        Assert-Check (Run-Evidence $e) 'tia.Siemens.Engineering.Safety' 'missing'
    }
    Run-Test 'Denied API enumeration is unavailable rather than absent' {
        $e = New-Evidence
        $e.probe.tiaV21.assemblies = @()
        $e.probe.tiaV21.enumerationStatus = 'unavailable'
        Assert-Check (Run-Evidence $e) 'tia.Siemens.Engineering.Base' 'unavailable'
    }
    Run-Test 'Restricted token membership does not imply user is absent from group' {
        $e = New-Evidence
        $e.probe.currentToken.groups[0].authorizedInCurrentToken = $false
        $report = Run-Evidence $e
        Assert-Check $report 'token.openness' 'unavailable'
        if ($report.capabilities.offline.status -ne 'unknown') { throw 'Restricted token must leave authorization unknown.' }
    }
    Run-Test 'Failed SID lookup remains unavailable' {
        $e = New-Evidence
        $e.probe.currentToken.groups[0].status = 'unavailable'
        Assert-Check (Run-Evidence $e) 'token.openness' 'unavailable'
    }
    Run-Test 'Absent Advanced remains optional for offline deployment' {
        $e = New-Evidence
        $e.probe.simulators.advanced.apiVersions = @()
        $e.probe.simulators.advanced.runtimeManager.status = 'missing'
        $report = Run-Evidence $e
        if ($report.selectedPrerequisites.status -ne 'present' -or $report.capabilities.simulation.status -ne 'blocked') { throw 'Optional simulation contaminated offline result.' }
        $required = Run-Evidence $e -RequireSimulation
        if ($required.selectedPrerequisites.status -ne 'blocked') { throw 'Required simulation ignored.' }
    }
    Run-Test 'Wrong Advanced API assembly version rejected even in V8 directory' {
        $e = New-Evidence
        $e.probe.simulators.advanced.apiVersions[0].x64.assembly.version = '7.0.0.1'
        Assert-Check (Run-Evidence $e) 'simulation.api.v8.x64' 'mismatch'
    }
    Run-Test 'Package file and runtime configuration prerequisites inspected' {
        $e = New-Evidence
        Add-PackageEvidence $e
        $report = Run-Evidence $e
        Assert-Check $report 'package.runtimeconfig' 'present'
        Assert-Check $report 'package.workers/openness/TiaMcp.Openness.V21.exe' 'present'
    }
    Run-Test 'Missing package host executable blocks deployment' {
        $e = New-Evidence
        Add-PackageEvidence $e
        $e.host.package.files[0].metadata.status = 'missing'
        $report = Run-Evidence $e
        Assert-Check $report 'package.host/TiaMcp.Host.exe' 'missing'
        if ($report.selectedPrerequisites.status -ne 'blocked') { throw 'Missing package file not blocked.' }
    }
    Run-Test 'Missing package evidence cannot silently skip required file' {
        $e = New-Evidence
        Add-PackageEvidence $e
        $e.host.package.files = @($e.host.package.files | Select-Object -Skip 1)
        Assert-Check (Run-Evidence $e) 'package.host/TiaMcp.Host.exe' 'unavailable'
    }
    Run-Test 'Bundle minimum patch overrides baseline requirement' {
        $e = New-Evidence
        Add-PackageEvidence $e
        $e.host.package.runtimeConfig.runtimeOptions.frameworks[0].version = '10.0.13'
        Assert-Check (Run-Evidence $e) 'dotnet.Microsoft.NETCore.App' 'missing'
    }
    Run-Test 'Custom runtime roll-forward cannot be silently certified' {
        $e = New-Evidence
        Add-PackageEvidence $e
        $e.host.package.runtimeConfig.runtimeOptions | Add-Member -NotePropertyName rollForward -NotePropertyValue 'Disable'
        Assert-Check (Run-Evidence $e) 'package.runtimeconfig' 'unavailable'
    }
    Run-Test 'Supplied evidence cannot trigger installation path probes' {
        [IO.File]::WriteAllText($fixturePath, ((New-Evidence) | ConvertTo-Json -Depth 20))
        foreach ($name in @('BundlePath','DotnetRoot','TiaApiDirectory','AdvancedSimulatorDirectory','AdvancedRuntimeDirectory')) {
            $overrides = @{$name='C:\should-not-be-probed'}
            $rejected = $false
            try { & $doctor -EvidencePath $fixturePath @overrides | Out-Null } catch { $rejected = $true }
            if (-not $rejected) { throw "Evidence and $name accepted together." }
        }
    }
    Run-Test 'Partial evidence produces unknown rather than success' {
        $report = Run-Evidence ([pscustomobject]@{schemaVersion=1;host=[pscustomobject]@{};probe=[pscustomobject]@{}})
        if ($report.selectedPrerequisites.status -ne 'unknown') { throw 'Incomplete evidence certified prerequisites.' }
    }
    Run-Test 'JSON output is bounded and optional saved report matches stdout' {
        $savePath = Join-Path $testDirectory 'saved.json'
        [IO.File]::WriteAllText($fixturePath, ((New-Evidence) | ConvertTo-Json -Depth 20))
        $json = & $doctor -EvidencePath $fixturePath -OutputPath $savePath
        if ($json -ne [IO.File]::ReadAllText($savePath) -or [Text.Encoding]::UTF8.GetByteCount($json) -gt 65536) { throw 'Output contract failed.' }
    }
    $summary = [ordered]@{schemaVersion=1;status='passed';testsPassed=$results.Count;tests=@($results.ToArray());nativeWorkersStarted=$false}
    if ($ReportPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath), ($summary | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false))) }
    Write-Output ("PASS {0} environment diagnostic tests." -f $results.Count)
}
finally {
    # Remove only the exact files created by this run; no recursive deletion.
    foreach ($name in @('evidence.json','saved.json','Test-TiaMcpEnvironment.ps1','Probe-Environment.ps1')) {
        $path = Join-Path $testDirectory $name
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    }
    [IO.Directory]::Delete($testDirectory, $false)
}
