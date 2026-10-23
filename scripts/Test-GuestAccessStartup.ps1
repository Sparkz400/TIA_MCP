#Requires -Version 5.1
# Focused startup/native-process regression tests. The full guest setup script is
# never allowed beyond its read-only destination guard; no machine changes occur.
param([string]$Engine)
$ErrorActionPreference = 'Stop'
if (-not $Engine) { $Engine = if ($PSVersionTable.PSVersion.Major -ge 7) {'pwsh.exe'} else {'powershell.exe'} }
$enginePath = (Get-Command $Engine -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$productionPath = Join-Path $PSScriptRoot 'Enable-GuestAccess.ps1'
$source = [IO.File]::ReadAllText($productionPath)
$testRoot = Join-Path $repoRoot ('artifacts/guest-access-startup-' + [Guid]::NewGuid().ToString('N'))
$sourceDirectory = Join-Path $testRoot 'script folder'
$callerDirectory = Join-Path $testRoot 'caller folder'
$workingDirectory = Join-Path $testRoot 'unrelated cwd'
foreach ($directory in @($sourceDirectory,$callerDirectory,$workingDirectory)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
$probePath = Join-Path $testRoot 'startup-probe.json'
$fixturePath = Join-Path $sourceDirectory 'Enable-GuestAccess.ps1'
$checks = 0
function Assert-Test([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message }; $script:checks++ }
function Run-Child([string[]]$Arguments, [string]$WorkingDirectory) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $enginePath
    $start.Arguments = ($Arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' '
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(20000)) { $process.Kill(); throw 'Read-only regression child exceeded 20 seconds.' }
    $result = @{exitCode=$process.ExitCode;stdout=$stdoutTask.GetAwaiter().GetResult();stderr=$stderrTask.GetAwaiter().GetResult()}
    $process.Dispose()
    return $result
}
try {
    $insertion = '$computer = Get-CimInstance -ClassName Win32_ComputerSystem'
    Assert-Test (($source.Split(@($insertion),[StringSplitOptions]::None).Count - 1) -eq 1) 'Expected one destination guard CIM call.'
    # Intercept only the first read-only machine query, after the real parameter
    # binding and script-body path resolution. Return a non-VM model so the real
    # guard rejects execution before privilege, network, install or registry work.
    $mock = @'
function Get-CimInstance {
    param([string]$ClassName)
    if ($ClassName -ne 'Win32_ComputerSystem') { throw 'Unexpected CIM query in startup fixture.' }
    $observed = [ordered]@{
        scriptDirectory=$guestAccessScriptDirectory
        publicKeyPath=$PublicKeyPath
        reportPath=(Join-Path $guestAccessScriptDirectory 'connection.json')
        scriptPath=$PSCommandPath
        powershellVersion=$PSVersionTable.PSVersion.ToString()
    }
    [IO.File]::WriteAllText('__PROBE_PATH__', ($observed | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    return [pscustomobject]@{Manufacturer='TiaMcp regression fixture';Model='Nonvirtual test model'}
}
'@
    $mock = $mock.Replace('__PROBE_PATH__', $probePath.Replace("'","''"))
    [IO.File]::WriteAllText($fixturePath, $source.Replace($insertion, $mock + "`r`n" + $insertion), (New-Object Text.UTF8Encoding($false)))
    Assert-Test ($source -match '\$reportPath\s*=\s*Join-Path\s+\$guestAccessScriptDirectory\s+''connection\.json''') 'Report path does not use the resolved script directory.'
    $explicitKey = Join-Path $callerDirectory 'explicit key.pub'
    foreach ($invocation in @('direct-default','direct-explicit','caller-default','caller-explicit')) {
        if (Test-Path -LiteralPath $probePath) { Remove-Item -LiteralPath $probePath }
        $explicit = $invocation.EndsWith('explicit')
        $arguments = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','RemoteSigned','-File',$fixturePath)
        if ($explicit) { $arguments += @('-PublicKeyPath',$explicitKey) }
        if ($invocation.StartsWith('caller')) {
            $caller = Join-Path $callerDirectory 'Invoke-Fixture.ps1'
            $command = "& '" + $fixturePath.Replace("'","''") + "'"
            if ($explicit) { $command += " -PublicKeyPath '" + $explicitKey.Replace("'","''") + "'" }
            [IO.File]::WriteAllText($caller, "$command`r`nexit `$LASTEXITCODE`r`n", (New-Object Text.UTF8Encoding($false)))
            $arguments = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','RemoteSigned','-File',$caller)
        }
        $result = Run-Child $arguments $workingDirectory
        Assert-Test (Test-Path -LiteralPath $probePath) ($invocation + ' failed before reaching mocked CIM: ' + $result.stderr)
        $observed = [IO.File]::ReadAllText($probePath) | ConvertFrom-Json
        Assert-Test ($observed.scriptDirectory -ceq $sourceDirectory) ($invocation + ' resolved the caller/CWD instead of the script directory.')
        $expectedKey = if ($explicit) {$explicitKey} else {Join-Path $sourceDirectory 'tia-mcp-test.pub'}
        Assert-Test ($observed.publicKeyPath -ceq $expectedKey) ($invocation + ' changed the public-key path.')
        Assert-Test ($observed.reportPath -ceq (Join-Path $sourceDirectory 'connection.json')) ($invocation + ' changed the report path.')
        Assert-Test (($result.stderr + $result.stdout) -match 'observed model is not VMware') ($invocation + ' did not reach the expected non-VM guard rejection.')
        Assert-Test (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory 'connection.json'))) ($invocation + ' passed the guard and wrote a setup report unexpectedly.')
    }
    $parseErrors=$null; $tokens=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($productionPath,[ref]$tokens,[ref]$parseErrors)
    $functions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-SshdCheck'},$true))
    Assert-Test ($functions.Count -eq 1 -and $parseErrors.Count -eq 0) 'Could not extract the actual native-check function.'
    $nativeFixture = Join-Path $testRoot 'Native-Check.ps1'
    $nativeCases = @'
$ErrorActionPreference='Stop'
__FUNCTION__
try {
    $success = Invoke-SshdCheck $env:COMSPEC @('/d','/c','echo benign stderr 1>&2')
    if (($success -join ' ') -notmatch 'benign stderr') { throw 'Native stderr was not normalized into captured strings.' }
    if ($ErrorActionPreference -ne 'Stop') { throw 'Caller error preference was not restored after success.' }
    $rejected = $false
    try { Invoke-SshdCheck $env:COMSPEC @('/d','/c','echo expected failure 1>&2 & exit /b 7') | Out-Null }
    catch { $rejected = $_.Exception.Message -match 'sshd validation failed:.*expected failure' }
    if (-not $rejected) { throw 'Nonzero native exit was not normalized into the expected failure.' }
    if ($ErrorActionPreference -ne 'Stop') { throw 'Caller error preference was not restored after failure.' }
    [pscustomobject]@{passed=$true;powershellVersion=$PSVersionTable.PSVersion.ToString();nativeCases=2} | ConvertTo-Json -Compress
    exit 0
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
'@
    [IO.File]::WriteAllText($nativeFixture, $nativeCases.Replace('__FUNCTION__',$functions[0].Extent.Text), (New-Object Text.UTF8Encoding($false)))
    $native = Run-Child @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','RemoteSigned','-File',$nativeFixture) $workingDirectory
    Assert-Test ($native.exitCode -eq 0) ('Native diagnostic normalization failed: ' + $native.stderr)
    $nativeResult = $native.stdout | ConvertFrom-Json
    Assert-Test ($nativeResult.passed -eq $true -and $nativeResult.nativeCases -eq 2) 'Native normalization evidence is incomplete.'
    $securityFunction=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-SshSecurityDescriptor'},$true))
    Assert-Test ($securityFunction.Count -eq 1) 'Missing SSH permission descriptor function.'
    # Inspect actual production descriptors without applying any ACL to this host.
    . ([scriptblock]::Create($securityFunction[0].Extent.Text))
    foreach ($isDirectory in @($false,$true)) {
        $descriptor = New-SshSecurityDescriptor -Directory:$isDirectory
        $rules = @($descriptor.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
        Assert-Test $descriptor.AreAccessRulesProtected 'SSH ACL must exclude inherited broad ProgramData grants.'
        Assert-Test ($descriptor.GetOwner([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-32-544') 'SSH owner is not Administrators.'
        Assert-Test ($rules.Count -eq $(if ($isDirectory) {3} else {2})) 'Unexpected SSH permission grant.'
        foreach ($sid in @('S-1-5-32-544','S-1-5-18')) {
            $rule = @($rules | Where-Object { $_.IdentityReference.Value -eq $sid })
            Assert-Test ($rule.Count -eq 1 -and $rule[0].AccessControlType -eq 'Allow' -and $rule[0].FileSystemRights -eq 'FullControl') ('Missing administrator/system FullControl: ' + $sid)
            Assert-Test ($rule[0].InheritanceFlags -eq $(if ($isDirectory) {'ContainerInherit, ObjectInherit'} else {'None'})) 'Administrative permissions have incorrect inheritance.'
        }
        $readers = @($rules | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-11' })
        if ($isDirectory) {
            Assert-Test ($readers.Count -eq 1 -and $readers[0].FileSystemRights -eq 'ReadAndExecute, Synchronize' -and $readers[0].InheritanceFlags -eq 'None') 'Folder read access must not grant write access or propagate into private keys.'
        } else { Assert-Test ($readers.Count -eq 0) 'Sensitive files must not grant access to other authenticated users.' }
    }
    Assert-Test ($source.IndexOf("Record-AccessStage `$stage 'passed' @{hostKeyCount=") -lt $source.IndexOf('Start-Service -Name sshd')) 'Public host identity must be saved before service startup can fail.'
    Assert-Test ($source.IndexOf('Save-AccessReport # Retain original ACLs') -lt $source.IndexOf('Set-Acl -LiteralPath $directory')) 'Original service ACL evidence must be saved before correction.'
    $diagnosticFunctions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in @('Get-SshServiceDiagnostics','Invoke-SshStartupProbe')},$true))
    Assert-Test ($diagnosticFunctions.Count -eq 2) 'Missing service diagnostics or bounded startup probe.'
    $diagnosticFixture = Join-Path $testRoot 'Diagnostic-Check.ps1'
    $diagnosticCases = @'
$ErrorActionPreference='Stop'
__FUNCTIONS__
$fixtureRoot='__TEST_ROOT__'
$fixtureSource=Join-Path $fixtureRoot 'FakeSshd.cs'
$fixtureExecutable=Join-Path $fixtureRoot 'FakeSshd.exe'
$code=@"
using System;
using System.Threading;
public static class FakeSshd {
    public static int Main(string[] args) {
        Console.Error.WriteLine("probePid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
        if (args.Length != 5 || args[0] != "-D" || args[1] != "-ddd" || args[2] != "-e" || args[3] != "-f") return 99;
        if (args[4].EndsWith("wait fixture.conf")) Thread.Sleep(30000);
        Console.Error.WriteLine("synthetic startup diagnostic");
        return 7;
    }
}
"@
[IO.File]::WriteAllText($fixtureSource,$code)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:exe ('/out:' + $fixtureExecutable) $fixtureSource | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not build isolated diagnostic process fixture.' }
$exited=Invoke-SshStartupProbe $fixtureExecutable (Join-Path $fixtureRoot 'exit fixture.conf')
if (-not $exited.exitedWithinSixSeconds -or $exited.exitCode -ne 7 -or $exited.stoppedOwnedProbe -or $exited.stderr -notmatch 'synthetic startup diagnostic') { throw 'Foreground exit diagnostics were lost or incorrectly reported.' }
$bounded=Invoke-SshStartupProbe $fixtureExecutable (Join-Path $fixtureRoot 'wait fixture.conf')
if ($bounded.exitedWithinSixSeconds -or -not $bounded.stoppedOwnedProbe -or $bounded.stderr -notmatch 'probePid=([0-9]+)') { throw 'Foreground probe timeout was not reported.' }
$ownedProbeId=[int]$Matches[1]
$alive=$false
try { $remaining=[Diagnostics.Process]::GetProcessById($ownedProbeId); $alive=-not $remaining.HasExited; $remaining.Dispose() } catch [ArgumentException] {}
if ($alive) { throw 'Owned diagnostic probe was left running.' }
# Mock only system inventory in this isolated child; do not inspect or mutate the
# real host service, permissions, firewall, keys or event logs.
$script:fakeServiceUnavailable=$false
function Get-CimInstance { if ($script:fakeServiceUnavailable) { throw 'synthetic service access denied' }; return [pscustomobject]@{Name='sshd';State='Stopped';ExitCode=1067} }
function Get-Item { return [pscustomobject]@{VersionInfo=@{FileVersion='9.5.fixture';ProductVersion='9.5.fixture'}} }
function Get-ChildItem { return [pscustomobject]@{FullName='C:\fixture\ssh_host_ed25519_key'} }
function Get-Acl { return [pscustomobject]@{Sddl='fixture metadata only'} }
function Get-NetTCPConnection { throw 'synthetic listener access denied' }
function Get-WinEvent { throw 'synthetic event access denied' }
$snapshot=Get-SshServiceDiagnostics 'C:\fixture\OpenSSH' 'C:\fixture\ssh'
if ($snapshot.service.State -ne 'Stopped' -or $snapshot.service.ExitCode -ne 1067 -or $snapshot.versions.Count -ne 3 -or $snapshot.permissions.Count -ne 5) { throw 'Expected service/version/ACL evidence is missing.' }
if (@($snapshot.collectionErrors | Where-Object { $_ -like 'listeners:*' }).Count -ne 1 -or $snapshot.collectionErrors.Count -ne 5) { throw 'Unavailable listener/event evidence was lost.' }
$script:fakeServiceUnavailable=$true
$unavailable=Get-SshServiceDiagnostics 'C:\fixture\OpenSSH' 'C:\fixture\ssh'
if ($null -ne $unavailable.service -or @($unavailable.collectionErrors | Where-Object { $_ -like 'service:*' }).Count -ne 1) { throw 'Unavailable service state was mistaken for stopped/running.' }
[pscustomobject]@{passed=$true;diagnosticCases=6;powershellVersion=$PSVersionTable.PSVersion.ToString()} | ConvertTo-Json -Compress
'@
    $diagnosticCases=$diagnosticCases.Replace('__FUNCTIONS__',(($diagnosticFunctions | ForEach-Object {$_.Extent.Text}) -join "`r`n")).Replace('__TEST_ROOT__',$testRoot.Replace("'","''"))
    [IO.File]::WriteAllText($diagnosticFixture,$diagnosticCases,(New-Object Text.UTF8Encoding($false)))
    $diagnosticResult=Run-Child @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','RemoteSigned','-File',$diagnosticFixture) $workingDirectory
    Assert-Test ($diagnosticResult.exitCode -eq 0) ('Startup diagnostics failed: ' + $diagnosticResult.stderr)
    $diagnosticReport=$diagnosticResult.stdout | ConvertFrom-Json
    Assert-Test ($diagnosticReport.passed -eq $true -and $diagnosticReport.diagnosticCases -eq 6) 'Startup diagnostic evidence is incomplete.'
    Write-Output ("PASS $checks guest-access startup checks and 6 diagnostic cases on PowerShell " + $nativeResult.powershellVersion + ': file invocation, native stderr, SSH security descriptors, bounded child cleanup and unavailable service evidence; no machine changes.')
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $prefix = (Join-Path $repoRoot 'artifacts') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notmatch '^guest-access-startup-[a-f0-9]{32}$') { throw 'Test cleanup escaped the workspace.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
