#Requires -Version 5.1
<#
Run once in elevated Windows PowerShell INSIDE the explicitly selected disposable
VM. Installs Windows OpenSSH Server if needed, enables one key-only engineering
login from the host VMnet8 address, and records public connection evidence.
Microsoft references: https://learn.microsoft.com/windows-server/administration/openssh/openssh_install_firstuse
https://learn.microsoft.com/windows-server/administration/openssh/openssh_keymanagement
#>
[CmdletBinding()]
param(
    [string]$HostAddress = '192.168.75.1',
    [string]$ExpectedGuestMac = '00:0c:29:71:a9:4b',
    [string]$PublicKeyPath
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 can bind parameter defaults before PSScriptRoot is
# populated. Resolve sibling files only after entering the script body.
$guestAccessScriptDirectory = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($guestAccessScriptDirectory)) {
    $guestAccessScriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($guestAccessScriptPath)) { $guestAccessScriptPath = $MyInvocation.MyCommand.Path }
    if ([string]::IsNullOrWhiteSpace($guestAccessScriptPath)) { throw 'Cannot determine this script location. Run the saved script with powershell.exe -File.' }
    $guestAccessScriptDirectory = [IO.Path]::GetDirectoryName($guestAccessScriptPath)
}
if ([string]::IsNullOrWhiteSpace($PublicKeyPath)) { $PublicKeyPath = Join-Path $guestAccessScriptDirectory 'tia-mcp-test.pub' }
# All machine-changing operations are below these destination guards.
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitProcess) { throw 'Run in 64-bit Windows PowerShell inside the selected VM.' }
$computer = Get-CimInstance -ClassName Win32_ComputerSystem
if ($computer.Manufacturer -notmatch 'VMware' -and $computer.Model -notmatch 'VMware') { throw 'Refusing to change this machine: its observed model is not VMware.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Open Windows PowerShell as Administrator inside the VM, then rerun this command.' }
$parsedAddress = $null
if (-not [Net.IPAddress]::TryParse($HostAddress, [ref]$parsedAddress) -or $parsedAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $HostAddress -notmatch '^192\.168\.75\.1$') { throw 'This prepared script permits only the observed host VMnet8 address 192.168.75.1.' }
$normalizedMac = $ExpectedGuestMac.Replace(':','-').ToUpperInvariant()
$adapter = @(Get-NetAdapter | Where-Object { $_.MacAddress -eq $normalizedMac })
if ($adapter.Count -ne 1) { throw 'Selected guest MAC was not observed; refusing to configure a different VMware guest.' }
$guestAddresses = @(Get-NetIPAddress -InterfaceIndex $adapter[0].InterfaceIndex -AddressFamily IPv4 | Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' } | Select-Object -ExpandProperty IPAddress)
if ($guestAddresses.Count -eq 0) { throw 'The selected guest adapter has no usable IPv4 address.' }
$publicKey = [IO.File]::ReadAllText([IO.Path]::GetFullPath($PublicKeyPath)).Trim()
if ($publicKey -notmatch '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp256) ([A-Za-z0-9+/]+={0,2})(?: [^\r\n]*)?$') { throw 'Expected one plain OpenSSH public key; private keys and key options are not accepted.' }
$keyType = $Matches[1]
$keyMaterial = $Matches[2]
[void][Convert]::FromBase64String($keyMaterial)
$account = $identity.Name
$accountParts = $account -split '\\', 2
$login = if ($accountParts.Count -eq 2 -and $accountParts[0] -ieq [Environment]::MachineName) {$accountParts[1]} else {$account}
if ($login -match '[\r\n"\x00]') { throw 'Account name contains an unsupported configuration character.' }
$reportPath = Join-Path $guestAccessScriptDirectory 'connection.json'
if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
    $historyDirectory = Join-Path $guestAccessScriptDirectory 'access-history'
    [IO.Directory]::CreateDirectory($historyDirectory) | Out-Null
    [IO.File]::Copy($reportPath, (Join-Path $historyDirectory ([Guid]::NewGuid().ToString('N') + '.json')), $false)
}
$report = [ordered]@{
    schemaVersion=1; observedAtUtc=[DateTime]::UtcNow.ToString('O'); status='running'; ready=$false
    guest=@{computerName=[Environment]::MachineName;manufacturer=$computer.Manufacturer;model=$computer.Model;username=$login;windowsAccount=$account;userSid=$identity.User.Value;ipv4=$guestAddresses;macAddress=$ExpectedGuestMac}
    allowedHostAddress=$HostAddress; port=22; authentication='publickey'; publicHostKeys=@()
    stages=New-Object System.Collections.ArrayList; changes=New-Object System.Collections.ArrayList
    serviceDiagnosticsBefore=$null; serviceDiagnosticsAfter=$null; startupProbe=$null
}
function Save-AccessReport { [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false))) }
function Record-AccessStage([string]$Name, [string]$Status, $Details) {
    [void]$report.stages.Add(@{name=$Name;status=$Status;details=$Details}); Save-AccessReport
    Write-Host ($Status.ToUpperInvariant() + ' ' + $Name)
}
function New-SshSecurityDescriptor([switch]$Directory) {
    $acl = if ($Directory) { New-Object Security.AccessControl.DirectorySecurity } else { New-Object Security.AccessControl.FileSecurity }
    $acl.SetAccessRuleProtection($true, $false)
    $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $acl.SetOwner($administrators)
    foreach ($sid in @($administrators,$system)) {
        if ($Directory) {
            $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit, ObjectInherit','None','Allow')))
        } else { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','Allow'))) }
    }
    if ($Directory) {
        # Folder traversal/read only; never inherit this read grant into private keys.
        $authenticatedUsers = New-Object Security.Principal.SecurityIdentifier('S-1-5-11')
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($authenticatedUsers,'ReadAndExecute','None','None','Allow')))
    }
    return $acl
}
function Set-AdminOnlyFileAcl([string]$Path) {
    $acl = New-SshSecurityDescriptor
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Get-SshServiceDiagnostics([string]$SshDirectory, [string]$SshData) {
    $data = [ordered]@{observedAtUtc=[DateTime]::UtcNow.ToString('O');service=$null;versions=@();permissions=@();listeners=@();events=@();collectionErrors=@()}
    try { $data.service = Get-CimInstance -ClassName Win32_Service -Filter "Name='sshd'" | Select-Object Name,State,StartMode,StartName,PathName,ProcessId,ExitCode,ServiceSpecificExitCode }
    catch { $data.collectionErrors += 'service: ' + $_.Exception.Message }
    foreach ($name in @('sshd.exe','ssh.exe','libcrypto.dll')) {
        try {
            $file = Get-Item -LiteralPath (Join-Path $SshDirectory $name) -ErrorAction Stop
            $data.versions += @{name=$name;fileVersion=$file.VersionInfo.FileVersion;productVersion=$file.VersionInfo.ProductVersion}
        } catch { $data.collectionErrors += $name + ': ' + $_.Exception.Message }
    }
    # Record ACL metadata only, never the contents of private keys or authorized_keys.
    $paths = @($SshData, (Join-Path $SshData 'logs'), (Join-Path $SshData 'sshd_config'), (Join-Path $SshData 'administrators_authorized_keys'))
    try { $paths += @(Get-ChildItem -LiteralPath $SshData -File -Filter 'ssh_host_*_key' -ErrorAction Stop | Select-Object -ExpandProperty FullName) }
    catch { $data.collectionErrors += 'host-key listing: ' + $_.Exception.Message }
    foreach ($path in $paths) {
        try { $acl = Get-Acl -LiteralPath $path -ErrorAction Stop; $data.permissions += @{path=$path;sddl=$acl.Sddl} }
        catch { $data.collectionErrors += 'ACL ' + $path + ': ' + $_.Exception.Message }
    }
    try { $data.listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -eq 22 | Select-Object LocalAddress,LocalPort,OwningProcess) }
    catch { $data.collectionErrors += 'listeners: ' + $_.Exception.Message }
    foreach ($log in @('OpenSSH/Admin','OpenSSH/Operational','System','Application')) {
        try {
            $filter = @{LogName=$log;StartTime=(Get-Date).AddMinutes(-30)}
            if ($log -eq 'System') { $filter.ProviderName = 'Service Control Manager' }
            if ($log -eq 'Application') { $filter.Level = @(1,2,3) }
            $events = @(Get-WinEvent -FilterHashtable $filter -MaxEvents 30 -ErrorAction Stop)
            foreach ($event in $events) {
                $message = [string]$event.Message
                if (($log -eq 'System' -or $log -eq 'Application') -and $message -notmatch '(?i)ssh|libcrypto') { continue }
                if ($message.Length -gt 8000) { $message = $message.Substring(0,8000) }
                $data.events += @{log=$log;id=$event.Id;level=$event.LevelDisplayName;time=$event.TimeCreated.ToUniversalTime().ToString('O');message=$message}
            }
        } catch { $data.collectionErrors += $log + ': ' + $_.Exception.Message }
    }
    return $data
}
function Invoke-SshStartupProbe([string]$Executable, [string]$ConfigPath) {
    # Called only after a stopped service and absence of a port-22 listener have
    # been observed. Never replace the service with an unbounded foreground daemon.
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = '-D -ddd -e -f "' + $ConfigPath.Replace('"','\"') + '"'
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $exited = $process.WaitForExit(6000)
        if (-not $exited) {
            $process.Kill() # Only this exact diagnostic child is owned by this script.
            if (-not $process.WaitForExit(5000)) { throw 'Owned SSH startup probe did not stop after termination.' }
        }
        $out = $stdout.GetAwaiter().GetResult(); $err = $stderr.GetAwaiter().GetResult()
        return @{exitedWithinSixSeconds=$exited;exitCode=$process.ExitCode;stoppedOwnedProbe=(-not $exited);stdout=$out.Substring(0,[Math]::Min($out.Length,24000));stderr=$err.Substring(0,[Math]::Min($err.Length,24000));context='Foreground diagnostic under the elevated engineering user; not a service success or login qualification.'}
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}
function Invoke-SshdCheck([string]$Executable, [string[]]$Arguments) {
    # In Windows PowerShell, redirected native stderr becomes ErrorRecord output.
    # Capture diagnostics without treating stderr alone as a failing exit code.
    $previousErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $Executable @Arguments 2>&1
        $nativeExitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousErrorPreference }
    if ($nativeExitCode -ne 0) { throw ('sshd validation failed: ' + ($output -join ' ')) }
    return @($output | ForEach-Object { $_.ToString() })
}
Save-AccessReport
$stage = 'windows_openssh_capability'
try {
    $capability = Get-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0'
    $installedNow = $capability.State -ne 'Installed'
    if ($installedNow) {
        Write-Host 'Installing the Windows OpenSSH Server capability; Windows may contact its configured update source.'
        try { $installation = Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' }
        catch { throw ('Windows OpenSSH capability source is unavailable or installation failed. No download fallback was attempted. ' + $_.Exception.Message) }
        [void]$report.changes.Add(@{change='Windows OpenSSH Server capability installed';restartNeeded=$installation.RestartNeeded})
        if ($installation.RestartNeeded) { throw 'Windows reports a restart is needed to complete OpenSSH installation; rerun after restarting the guest.' }
    }
    $sshDirectory = Join-Path $env:WINDIR 'System32/OpenSSH'
    $sshd = Join-Path $sshDirectory 'sshd.exe'
    $keygen = Join-Path $sshDirectory 'ssh-keygen.exe'
    if (-not (Test-Path -LiteralPath $sshd -PathType Leaf) -or -not (Test-Path -LiteralPath $keygen -PathType Leaf)) { throw 'Installed OpenSSH executables were not found.' }
    Record-AccessStage $stage 'passed' @{installedNow=$installedNow;sshd=$sshd}

    $stage = 'restricted_firewall'
    # Windows capability installation creates this broad default rule. Restrict
    # its address filter before enabling sshd, and retain its former filter in evidence.
    $defaultRule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
    if ($defaultRule) {
        $oldAddresses = @($defaultRule | Get-NetFirewallAddressFilter | Select-Object -ExpandProperty RemoteAddress)
        $defaultRule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress $HostAddress | Out-Null
        [void]$report.changes.Add(@{change='Default OpenSSH firewall rule restricted';rule=$defaultRule.Name;previousRemoteAddresses=$oldAddresses;remoteAddress=$HostAddress})
    }
    $ruleName = 'TiaMcp-DisposableVm-SSH-HostOnly'
    if (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue) { Remove-NetFirewallRule -Name $ruleName }
    New-NetFirewallRule -Name $ruleName -DisplayName 'TIA MCP disposable VM SSH from host only' -Enabled True -Direction Inbound -Action Allow -Protocol TCP -LocalPort 22 -RemoteAddress $HostAddress -Program $sshd -Profile Any | Out-Null
    [void]$report.changes.Add(@{change='Dedicated SSH firewall rule configured';rule=$ruleName;remoteAddress=$HostAddress;localPort=22})
    Record-AccessStage $stage 'passed' @{managedRule=$ruleName;remoteAddress=$HostAddress;defaultRuleAlsoRestricted=[bool]$defaultRule}

    $stage = 'public_key_authorization'
    $sshData = Join-Path $env:ProgramData 'ssh'
    [IO.Directory]::CreateDirectory($sshData) | Out-Null
    $keysFile = Join-Path $sshData 'administrators_authorized_keys'
    # This command requires the current engineering account's elevated token;
    # Windows OpenSSH uses the administrators file for that account.
    $oldKeys = if (Test-Path -LiteralPath $keysFile) {[IO.File]::ReadAllText($keysFile)} else {''}
    $keyLine = 'from="' + $HostAddress + '",no-agent-forwarding,no-port-forwarding,no-X11-forwarding ' + $keyType + ' ' + $keyMaterial + ' tia-mcp-disposable-vm'
    if (-not (($oldKeys -split '\r?\n') -contains $keyLine)) {
        if ($oldKeys) { [IO.File]::Copy($keysFile, ($keysFile + '.tia-mcp-' + [Guid]::NewGuid().ToString('N') + '.bak'), $false) }
        [IO.File]::WriteAllText($keysFile, $oldKeys.TrimEnd("`r","`n") + $(if ($oldKeys) {"`r`n"} else {''}) + $keyLine + "`r`n", (New-Object Text.UTF8Encoding($false)))
        [void]$report.changes.Add(@{change='Dedicated public key appended';path=$keysFile;existingKeysPreserved=$true;restrictedSource=$HostAddress})
    }
    Set-AdminOnlyFileAcl $keysFile
    Record-AccessStage $stage 'passed' @{path=$keysFile;keyType=$keyType;restrictedAclSids=@('S-1-5-32-544','S-1-5-18')}

    $stage = 'sshd_configuration'
    $configPath = Join-Path $sshData 'sshd_config'
    if (-not (Test-Path -LiteralPath $configPath)) { [IO.File]::Copy((Join-Path $sshDirectory 'sshd_config_default'), $configPath, $false) }
    $originalConfig = [IO.File]::ReadAllText($configPath)
    $backup = $configPath + '.tia-mcp-' + [Guid]::NewGuid().ToString('N') + '.bak'
    [IO.File]::Copy($configPath, $backup, $false)
    $begin = '# BEGIN TIA MCP DISPOSABLE VM ACCESS'
    $end = '# END TIA MCP DISPOSABLE VM ACCESS'
    $preserved = [regex]::Replace($originalConfig, '(?ms)^# BEGIN TIA MCP DISPOSABLE VM ACCESS\r?\n.*?^# END TIA MCP DISPOSABLE VM ACCESS\r?\n?', '')
    $quotedLogin = $login.ToLowerInvariant().Replace('\','\\')
    $block = @($begin, ('Match User "' + $quotedLogin + '"'), '    PubkeyAuthentication yes', '    AuthenticationMethods publickey', '    PasswordAuthentication no', ('    AuthorizedKeysFile "' + $keysFile.Replace('\','/') + '"'), $end) -join "`r`n"
    [IO.File]::WriteAllText($configPath, $preserved.TrimEnd("`r","`n") + "`r`n`r`n" + $block + "`r`n", (New-Object Text.UTF8Encoding($false)))
    & $keygen -A | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'OpenSSH host-key generation failed.' }
    try {
        Invoke-SshdCheck $sshd @('-t','-f',$configPath) | Out-Null
        $effective = Invoke-SshdCheck $sshd @('-T','-f',$configPath,'-C',('user=' + $login.ToLowerInvariant() + ',host=' + [Environment]::MachineName + ',addr=' + $HostAddress))
        if ($effective -notcontains 'pubkeyauthentication yes' -or $effective -notcontains 'passwordauthentication no' -or $effective -notcontains 'authenticationmethods publickey') { throw 'Existing SSH configuration prevents the dedicated public-key-only settings from taking effect.' }
    } catch { [IO.File]::WriteAllText($configPath, $originalConfig, (New-Object Text.UTF8Encoding($false))); throw }
    [void]$report.changes.Add(@{change='Dedicated Match-user block added';config=$configPath;backup=$backup;existingConfigurationPreserved=$true})
    Record-AccessStage $stage 'passed' @{syntaxValidated=$true;effectiveKeyOnlyAuthentication=$true;user=$login;backup=$backup}

    $stage = 'openness_group'
    $group = Get-LocalGroup -Name 'Siemens TIA Openness' -ErrorAction Stop
    $members = @(Get-LocalGroupMember -Group $group)
    $alreadyMember = @($members | Where-Object { $_.SID.Value -eq $identity.User.Value }).Count -gt 0
    if (-not $alreadyMember) { Add-LocalGroupMember -Group $group -Member $account; [void]$report.changes.Add(@{change='Existing Siemens TIA Openness group membership added';account=$account;sid=$identity.User.Value;group=$group.Name}) }
    Record-AccessStage $stage 'passed' @{group=$group.Name;account=$account;added=(-not $alreadyMember);freshTokenRequired=$true;existingDesktopTokenChanged=$false}

    $stage = 'ssh_service_permissions'
    $report.serviceDiagnosticsBefore = Get-SshServiceDiagnostics $sshDirectory $sshData
    Save-AccessReport # Retain original ACLs and service errors before changing them.
    # The directory was created before the service's first start and may have
    # inherited ProgramData write grants. OpenSSH rejects overly broad permissions.
    # Microsoft: https://learn.microsoft.com/troubleshoot/windows-server/system-management-components/error-1053-1067-7034-after-update-openssh-doesnt-start
    foreach ($directory in @($sshData, (Join-Path $sshData 'logs'))) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        Set-Acl -LiteralPath $directory -AclObject (New-SshSecurityDescriptor -Directory)
    }
    $securedFiles = @($configPath,$keysFile)
    $securedFiles += @(Get-ChildItem -LiteralPath $sshData -File -Filter 'ssh_host_*_key' | Select-Object -ExpandProperty FullName)
    foreach ($file in $securedFiles) { Set-AdminOnlyFileAcl $file }
    [void]$report.changes.Add(@{change='SSH service directory and sensitive-file permissions normalized';directories=@($sshData,(Join-Path $sshData 'logs'));files=$securedFiles;writeAccessSids=@('S-1-5-32-544','S-1-5-18');directoryReadSid='S-1-5-11';directoryReadInherited=$false;priorPermissions='serviceDiagnosticsBefore.permissions'})
    Invoke-SshdCheck $sshd @('-t','-f',$configPath) | Out-Null
    Record-AccessStage $stage 'passed' @{directories=2;sensitiveFiles=$securedFiles.Count;syntaxValidated=$true}

    $stage = 'public_host_identity'
    $report.publicHostKeys = @(Get-ChildItem -LiteralPath $sshData -File -Filter 'ssh_host_*_key.pub' | ForEach-Object { @{name=$_.Name;publicKey=[IO.File]::ReadAllText($_.FullName).Trim()} })
    if ($report.publicHostKeys.Count -eq 0) { throw 'No public SSH host keys were available for verified client pinning.' }
    Record-AccessStage $stage 'passed' @{hostKeyCount=$report.publicHostKeys.Count}

    $stage = 'sshd_service'
    Set-Service -Name sshd -StartupType Automatic
    $service = Get-Service -Name sshd
    if ($service.Status -eq 'Running') { Restart-Service -Name sshd } else { Start-Service -Name sshd }
    $service = Get-Service -Name sshd
    if ($service.Status -ne 'Running') { throw 'sshd service did not reach Running state.' }
    $report.serviceDiagnosticsAfter = Get-SshServiceDiagnostics $sshDirectory $sshData
    [void]$report.changes.Add(@{change='sshd service enabled';startupType='Automatic';status='Running'})
    Record-AccessStage $stage 'passed' @{service='Running';hostKeyCount=$report.publicHostKeys.Count;remoteLogin='unverified until the host authenticates using its dedicated private key'}
    $report.status = 'passed'; $report.ready = $true
} catch {
    $failure = $_.Exception.ToString()
    $report.status = 'blocked'; $report.ready = $false
    Record-AccessStage $stage 'blocked' $failure
    if ($sshd -and $sshData) {
        try {
            $report.serviceDiagnosticsAfter = Get-SshServiceDiagnostics $sshDirectory $sshData
            if ($stage -eq 'sshd_service' -and $report.serviceDiagnosticsAfter.service.State -eq 'Stopped' -and $report.serviceDiagnosticsAfter.listeners.Count -eq 0 -and @($report.serviceDiagnosticsAfter.collectionErrors | Where-Object { $_ -like 'listeners:*' }).Count -eq 0) {
                $report.startupProbe = Invoke-SshStartupProbe $sshd $configPath
            }
        } catch { $report.startupProbe = @{diagnosticError=$_.Exception.ToString()} }
    }
} finally {
    $report.finishedAtUtc = [DateTime]::UtcNow.ToString('O')
    Save-AccessReport
}
[pscustomobject]@{status=$report.status;ready=$report.ready;connectionReport=$reportPath;username=$login;ipv4=$guestAddresses;port=22} | ConvertTo-Json -Depth 4
if ($report.ready) { exit 0 }
exit 2
