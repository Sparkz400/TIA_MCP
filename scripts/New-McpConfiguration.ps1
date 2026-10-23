param(
    [Parameter(Mandatory=$true)][string]$BundlePath,
    [string]$SshHost,
    [string]$ArtifactsDirectory,
    [string]$TiaApiDirectory,
    [string]$SimulatorApiDirectory,
    [string]$GitExecutable,
    [ValidateSet('Direct','InteractiveUser')][string]$SessionMode = 'Direct',
    [ValidateRange(5,900)][int]$TimeoutSeconds = 300,
    [ValidateRange(1,120)][int]$ConnectTimeoutSeconds = 15,
    [switch]$StrictBundle
)
$ErrorActionPreference = 'Stop'
function Assert-WindowsPath([string]$Path, [string]$Label) {
    if (-not ($Path -match '^[A-Za-z]:[\\/]' -or $Path -match '^\\\\[^\\/?\.][^\\/]*[\\/][^\\/]+([\\/]|$)') -or
        $Path -match '[\x00-\x1f"<>|?*]' -or $Path.Substring(2).Contains(':') -or
        @($Path -split '[\\/]' | Where-Object { $_ -eq '.' -or $_ -eq '..' -or ($_ -and ($_ -match '[. ]$')) }).Count -gt 0) {
        throw "$Label must be a fully qualified Windows path without device syntax, traversal, alternate streams, control characters or invalid filename characters."
    }
}
Assert-WindowsPath $BundlePath 'BundlePath'
foreach ($override in @(@{value=$ArtifactsDirectory; name='ArtifactsDirectory'}, @{value=$TiaApiDirectory; name='TiaApiDirectory'}, @{value=$SimulatorApiDirectory; name='SimulatorApiDirectory'}, @{value=$GitExecutable; name='GitExecutable'})) {
    if ($override.value) { Assert-WindowsPath $override.value $override.name }
}
$launcher = $BundlePath.TrimEnd('\','/') + '\scripts\Start-TiaMcp.ps1'
$launcherArguments = @('-TimeoutSeconds', $TimeoutSeconds.ToString())
foreach ($override in @(@{value=$ArtifactsDirectory; name='ArtifactsDirectory'}, @{value=$TiaApiDirectory; name='TiaApiDirectory'}, @{value=$SimulatorApiDirectory; name='SimulatorApiDirectory'}, @{value=$GitExecutable; name='GitExecutable'})) {
    if ($override.value) { $launcherArguments += @(('-' + $override.name), $override.value) }
}
if ($StrictBundle) { $launcherArguments += '-StrictBundle' }
if ($SessionMode -eq 'InteractiveUser') { $launcherArguments += '-InteractiveSession' }
if ($SshHost) {
    if ($SshHost -notmatch '^(?:[a-zA-Z0-9_][a-zA-Z0-9_.-]*@)?[a-zA-Z0-9_][a-zA-Z0-9_.-]*$') {
        throw 'SshHost must be a host alias or user@hostname from your existing OpenSSH configuration.'
    }
    # OpenSSH's Windows default shell may be cmd or PowerShell. A UTF-16 encoded
    # command avoids a second shell parsing any caller-supplied path as syntax.
    $literal = "'" + $launcher.Replace("'", "''") + "'"
    $remoteScript = '& ' + $literal
    for ($index = 0; $index -lt $launcherArguments.Count; $index++) {
        $argument = $launcherArguments[$index]
        if ($argument.StartsWith('-')) { $remoteScript += ' ' + $argument }
        else { $remoteScript += " '" + $argument.Replace("'", "''") + "'" }
    }
    $remoteScript += '; exit $LASTEXITCODE'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remoteScript))
    $remoteCommand = 'powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -EncodedCommand ' + $encoded
    $server = @{ command = 'ssh.exe'; args = @('-T', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o', ('ConnectTimeout=' + $ConnectTimeoutSeconds), '-o', 'ConnectionAttempts=1', '-o', 'ServerAliveInterval=15', '-o', 'ServerAliveCountMax=3', '--', $SshHost, $remoteCommand) }
} else {
    $server = @{ command = 'powershell.exe'; args = @('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'RemoteSigned', '-File', $launcher) + $launcherArguments }
}
@{ mcpServers = @{ 'tia-v21' = $server } } | ConvertTo-Json -Depth 6
