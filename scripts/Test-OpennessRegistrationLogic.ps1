#Requires -Version 5.1
# Exercises production selection/write helpers with an in-memory registry, and
# setup orchestration with a stub registration script. Never changes Windows trust.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpennessRegistration.Helpers.ps1')

class RegistrationTestKey {
    [hashtable]$Store
    [string]$Path
    RegistrationTestKey([hashtable]$store, [string]$path) { $this.Store=$store; $this.Path=$path }
    [object] OpenSubKey([string]$name, [bool]$writable) {
        $fullKeyPath = if ($this.Path) { $this.Path + '\' + $name } else { $name }
        if (-not $this.Store.ContainsKey($fullKeyPath)) { return $null }
        return [RegistrationTestKey]::new($this.Store,$fullKeyPath)
    }
    [object] CreateSubKey([string]$name, [bool]$writable) {
        $fullKeyPath = if ($this.Path) { $this.Path + '\' + $name } else { $name }
        if (-not $this.Store.ContainsKey($fullKeyPath)) { $this.Store[$fullKeyPath]=@{} }
        $this.Store.__writes++
        return [RegistrationTestKey]::new($this.Store,$fullKeyPath)
    }
    [string[]] GetSubKeyNames() {
        $prefix=$this.Path + '\'
        $names=New-Object 'System.Collections.Generic.List[string]'
        foreach ($key in $this.Store.Keys) {
            if ($key.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) {
                $name=$key.Substring($prefix.Length)
                if (-not $name.Contains('\')) { $names.Add($name) }
            }
        }
        return $names.ToArray()
    }
    [string[]] GetValueNames() { return @($this.Store[$this.Path].Keys) }
    [object] GetValue([string]$name, $fallback, $options) {
        if (-not $this.Store[$this.Path].ContainsKey($name)) { return $fallback }
        return $this.Store[$this.Path][$name].value
    }
    [object] GetValueKind([string]$name) { return $this.Store[$this.Path][$name].kind }
    [void] SetValue([string]$name, $value, $kind) {
        if ($this.Store.__failHash -and $name -eq 'FileHash') { throw 'Injected partial write failure' }
        if ($this.Store.__corruptHash -and $name -eq 'FileHash') { $value='corrupted' }
        $this.Store[$this.Path][$name]=@{value=$value;kind=$kind.ToString()}
        $this.Store.__writes++
    }
    [void] Flush() {}
    [void] Dispose() {}
}

$application='SOFTWARE\Siemens\Automation\Openness\AllowList\TiaMcp.Openness.V21.exe'
$proposal=[ordered]@{Path='C:\TiaMcp\workers\openness\TiaMcp.Openness.V21.exe';DateModified='2026/11/02 12:00:00.123';FileHash='approved-hash'}
$checks=0
function Check([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function New-TestRegistry {
    return [RegistrationTestKey]::new(@{__writes=0;__failHash=$false;__corruptHash=$false},'')
}
function Add-TestEntry($Base,[string]$Name,$Values) {
    $Base.Store[$application]=@{}
    $key=@{}
    foreach ($valueName in $Values.Keys) { $key[$valueName]=@{kind='String';value=$Values[$valueName]} }
    $Base.Store[$application + '\' + $Name]=$key
}

$base=New-TestRegistry
Check ((Select-OpennessEntry $base $application $proposal) -ceq ($application + '\Entry')) 'First setup should propose Entry.'
Check ($base.Store.__writes -eq 0) 'Selection must not create the application key.'
Add-TestEntry $base 'Entry' @{Path='D:\AnotherInstall\TiaMcp.Openness.V21.exe';DateModified='other-date';FileHash='other-hash';Unrelated='keep'}
$otherBefore=$base.Store[$application + '\Entry'] | ConvertTo-Json -Compress
Add-TestEntry $base 'Entry (1)' @{Path=$proposal.Path.ToUpperInvariant();DateModified='old';FileHash='old';Unrelated='keep-target-value'}
$selected=Select-OpennessEntry $base $application $proposal
Check ($selected -ceq ($application + '\Entry (1)')) 'Update must select the same path regardless of case, not Entry.'
$previous=Read-OpennessEntry $base $selected
Check (-not (Test-OpennessEntryMatches $previous $proposal)) 'Stale identity should require refresh.'
$result=Write-OpennessEntry $base $selected $proposal
Check (Test-OpennessEntryMatches $result $proposal) 'Refresh must verify all three String values.'
Check (($base.Store[$application + '\Entry'] | ConvertTo-Json -Compress) -ceq $otherBefore) 'Refresh changed another installation.'
Check ($base.Store[$selected].Unrelated.value -ceq 'keep-target-value') 'Refresh removed an unrelated target value.'
$writes=$base.Store.__writes
Check ((Select-OpennessEntry $base $application $proposal) -ceq $selected) 'Repeated setup must retain the current identity.'
Check ((Test-OpennessEntryMatches (Read-OpennessEntry $base $selected) $proposal) -and $base.Store.__writes -eq $writes) 'Current identity should be a no-write result.'

# Historical duplicate appears before the matching current entry.
Add-TestEntry $base 'Entry' @{Path=$proposal.Path;DateModified='old';FileHash='old'}
Check ((Select-OpennessEntry $base $application $proposal) -ceq $selected) 'Stale duplicate hid a valid later entry.'
$base.Store[$selected].FileHash.kind='ExpandString'
Check (-not (Test-OpennessEntryMatches (Read-OpennessEntry $base $selected) $proposal)) 'Non-REG_SZ values must not be trusted.'

$base=New-TestRegistry
Add-TestEntry $base 'Entry' @{Path='D:\One\worker.exe'}
Add-TestEntry $base 'Entry (2)' @{Path='D:\Two\worker.exe'}
Check ((Select-OpennessEntry $base $application $proposal) -ceq ($application + '\Entry (1)')) 'Relocation must allocate an unused entry and preserve existing paths.'

foreach ($failure in @('__failHash','__corruptHash')) {
    $base=New-TestRegistry
    Add-TestEntry $base 'Entry' @{Path=$proposal.Path;DateModified='old';FileHash='old';Unrelated='keep'}
    $base.Store[$failure]=$true
    $failed=$false
    $audit=@{written=$false}
    try { Write-OpennessEntry $base ($application + '\Entry') $proposal { $audit.written=$true } | Out-Null } catch { $failed=$true }
    Check $failed ('Write/readback failure was hidden: ' + $failure)
    Check ($audit.written -eq ($failure -eq '__corruptHash')) 'Write completion evidence must distinguish partial writes from failed readback.'
    Check ($base.Store[$application + '\Entry'].Unrelated.value -ceq 'keep') 'Failed refresh damaged unrelated values.'
}

# The production setup script must only create configuration after verified registration.
# Its sibling registrar is replaced in this isolated fixture; apply is NEVER called against HKLM.
$setupRoot=Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/setup-orchestration-' + [Guid]::NewGuid().ToString('N'))
$fixtureScripts=Join-Path $setupRoot 'scripts'
$fixtureBundle=Join-Path $setupRoot 'bundle'
[void][IO.Directory]::CreateDirectory($fixtureScripts)
[void][IO.Directory]::CreateDirectory($fixtureBundle)
foreach ($fileName in @('Setup-TiaMcp.ps1','New-McpConfiguration.ps1','OpennessRegistration.Helpers.ps1')) {
    [IO.File]::Copy((Join-Path $PSScriptRoot $fileName),(Join-Path $fixtureScripts $fileName))
}
$stub=@'
param($BundlePath,$ExpectedWorkerSha256,$ReportPath,[switch]$InspectOnly)
$state=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'state.txt'))
$result=@{status=$state;readBackVerified=($state -in @('registered','already_registered'));expectedWorkerSha256=$ExpectedWorkerSha256;bundlePath=$BundlePath;registryWritesAttempted=$false}
if (-not $InspectOnly) { $result | ConvertTo-Json | Set-Content -LiteralPath $ReportPath -Encoding UTF8 }
$result | ConvertTo-Json
'@
[IO.File]::WriteAllText((Join-Path $fixtureScripts 'Register-TiaOpennessApplication.ps1'),$stub)
$setup=Join-Path $fixtureScripts 'Setup-TiaMcp.ps1'
$setupArgs=@{BundlePath=$fixtureBundle;ExpectedWorkerSha256=('1'*64);ArtifactsDirectory=(Join-Path $setupRoot 'data')}
foreach ($state in @('registered','already_registered','failed')) {
    [IO.File]::WriteAllText((Join-Path $fixtureScripts 'state.txt'),$state)
    $configurationFile=Join-Path $setupRoot ($state + '-client.json')
    $reportFile=Join-Path $setupRoot ($state + '-report.json')
    $failed=$false
    try { $result=& $setup @setupArgs -ConfigurationPath $configurationFile -ReportPath $reportFile | ConvertFrom-Json } catch { $failed=$true }
    if ($state -eq 'failed') {
        Check ($failed -and -not (Test-Path -LiteralPath $configurationFile)) 'Unverified registration must not produce a configuration.'
        Check (Test-Path -LiteralPath $reportFile) 'A failed registration report must remain available.'
    } else {
        Check (-not $failed -and $result.configurationWritten -and $result.registration.expectedWorkerSha256 -ceq ('1'*64)) 'Setup did not forward the approved hash or complete configuration.'
        $saved=Get-Content -LiteralPath $configurationFile -Raw | ConvertFrom-Json
        Check ($saved.mcpServers.'tia-v21'.args -contains ($fixtureBundle + '\scripts\Start-TiaMcp.ps1')) 'Setup configuration lost its final installed path.'
        $before=[IO.File]::ReadAllText($configurationFile)
        $rejected=$false
        try { & $setup @setupArgs -ConfigurationPath $configurationFile -ReportPath (Join-Path $setupRoot 'unexpected-report.json') | Out-Null } catch { $rejected=$true }
        Check ($rejected -and [IO.File]::ReadAllText($configurationFile) -ceq $before -and -not (Test-Path -LiteralPath (Join-Path $setupRoot 'unexpected-report.json'))) 'Existing configuration must be refused before registration.'
    }
}
Write-Output "PASS $checks registration/update and setup orchestration checks; in-memory registry and stub registrar only."
