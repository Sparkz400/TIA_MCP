param(
    [string]$ArtifactsDirectory,
    [string]$TiaApiDirectory,
    [string]$SimulatorApiDirectory,
    [string]$GitExecutable,
    [ValidateRange(5,900)][int]$TimeoutSeconds = 300,
    [switch]$StrictBundle,
    [switch]$InteractiveSession,
    [switch]$Diagnose
)
$ErrorActionPreference = 'Stop'
$bundleRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$hostExecutable = Join-Path $bundleRoot 'host/TiaMcp.Host.exe'
$engineeringWorker = Join-Path $bundleRoot 'workers/openness/TiaMcp.Openness.V21.exe'
$simulatorWorker = Join-Path $bundleRoot 'workers/simulator/TiaMcp.Simulator.V8.exe'
if (-not (Test-Path -LiteralPath $hostExecutable -PathType Leaf)) { throw 'Run Package.ps1 first and use this script from the resulting bundle.' }
# The launcher is bundle-only; the developer checkout uses Run.ps1. Suppress the
# verifier result because stdout belongs exclusively to the MCP protocol.
$verification = & (Join-Path $PSScriptRoot 'Verify-TiaMcpBundle.ps1') -BundlePath $bundleRoot -Strict:$StrictBundle
if (-not $verification.verified) { throw 'Bundle verification did not complete.' }
if ($verification.bundleKind -eq 'offline' -and ($Diagnose -or $InteractiveSession -or $TiaApiDirectory -or $SimulatorApiDirectory)) { throw 'Offline bundles do not support native diagnosis, API overrides or interactive engineering sessions.' }
if (-not $ArtifactsDirectory) {
    if (-not $env:LOCALAPPDATA) { throw 'LOCALAPPDATA is unavailable; supply an absolute ArtifactsDirectory outside the bundle.' }
    $ArtifactsDirectory = Join-Path $env:LOCALAPPDATA 'TiaMcp/artifacts'
}
foreach ($directory in @($ArtifactsDirectory, $TiaApiDirectory, $SimulatorApiDirectory, $GitExecutable)) {
    if ($directory -and ($directory -notmatch '^([A-Za-z]:[\\/]|\\\\[^\\/?\.][^\\/]*[\\/][^\\/]+([\\/]|$))' -or $directory -match '[\x00-\x1f"<>|?*]' -or $directory.Substring(2).Contains(':'))) {
        throw 'Artifact and API directories must be fully qualified Windows paths without device syntax or alternate streams.'
    }
}
$ArtifactsDirectory = [IO.Path]::GetFullPath($ArtifactsDirectory)
$bundlePrefix = $bundleRoot.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
if ($ArtifactsDirectory.Equals($bundleRoot, [StringComparison]::OrdinalIgnoreCase) -or $ArtifactsDirectory.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ArtifactsDirectory must be outside the immutable release bundle. Regenerate the MCP configuration with an external artifact directory.'
}
# Reject an existing junction/symlink chain so an external-looking artifact path
# cannot redirect writes back into the bundle or another unexpected location.
$ancestor = $ArtifactsDirectory
while ($ancestor) {
    if (Test-Path -LiteralPath $ancestor) {
        if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'ArtifactsDirectory must not traverse a reparse point.' }
    }
    $parentDirectory = [IO.Directory]::GetParent($ancestor)
    $ancestor = if ($parentDirectory) { $parentDirectory.FullName } else { $null }
}
$launchArguments = @('--worker', $engineeringWorker, '--sim-worker', $simulatorWorker, '--artifacts', $ArtifactsDirectory, '--timeout-seconds', $TimeoutSeconds.ToString())
if ($verification.bundleKind -eq 'offline') { $launchArguments += '--no-native-workers' }
if ($GitExecutable) { $launchArguments += @('--git-executable', $GitExecutable) }
if ($Diagnose) { $launchArguments += '--diagnose' }
if ($TiaApiDirectory) { $launchArguments += @('--api-dir', $TiaApiDirectory) }
if ($SimulatorApiDirectory) { $launchArguments += @('--sim-api-dir', $SimulatorApiDirectory) }
if ($InteractiveSession) {
    if ($Diagnose) { throw 'Interactive session mode exposes MCP stdio; use the normal tools for diagnostics.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Interactive session mode requires an elevated engineering account to register its transient task.' }
    $channel = [Guid]::NewGuid().ToString('N')
    $taskName = 'TiaMcp-Session-' + $channel
    $sessionDirectory = Join-Path $ArtifactsDirectory ('sessions/' + $channel)
    [IO.Directory]::CreateDirectory($sessionDirectory) | Out-Null
    # The task uses an elevated interactive token. Its settings must not inherit
    # write access from a shared artifact directory owned by another local user.
    $sessionAcl = New-Object Security.AccessControl.DirectorySecurity
    $sessionAcl.SetAccessRuleProtection($true,$false)
    $sessionAcl.SetOwner($identity.User)
    foreach ($sid in @($identity.User,(New-Object Security.Principal.SecurityIdentifier('S-1-5-18')))) {
        $sessionAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit, ObjectInherit','None','Allow')))
    }
    Set-Acl -LiteralPath $sessionDirectory -AclObject $sessionAcl
    $settingsPath = Join-Path $sessionDirectory 'relay.json'
    @{channel=$channel;userSid=$identity.User.Value;hostArguments=$launchArguments;receiptPath=(Join-Path $sessionDirectory 'receipt.json');shutdownTimeoutSeconds=($TimeoutSeconds + 30)} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    $serverCommand = '& ''' + $hostExecutable.Replace("'","''") + ''' --session-relay-server ''' + $settingsPath.Replace("'","''") + '''; exit $LASTEXITCODE'
    $encodedServer = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($serverCommand))
    $action = New-ScheduledTaskAction -Execute (Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe') -Argument ('-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand ' + $encodedServer)
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Highest
    $taskSettings = New-ScheduledTaskSettingsSet -Hidden -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $registered = $false
    try {
        Register-ScheduledTask -TaskName $taskName -Description ('TIA MCP session relay ' + $channel) -Action $action -Principal $taskPrincipal -Settings $taskSettings -ErrorAction Stop | Out-Null
        $registered = $true
        Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
        & $hostExecutable --session-relay-client $settingsPath
        $relayExit = $LASTEXITCODE
    } finally {
        # Deleting the exact owned task does not terminate its running instance.
        # Pipe EOF handles shutdown and preserves uncertain native outcomes.
        if ($registered) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue }
    }
    exit $relayExit
}
& $hostExecutable @launchArguments
exit $LASTEXITCODE
