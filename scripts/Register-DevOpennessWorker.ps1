<#
.SYNOPSIS
Keep Debug-built V21 workers in Siemens' Openness AllowList so TIA stops prompting after every build.

.DESCRIPTION
Default mode, run by every Debug build of TiaMcp.Openness.V21: writes Path, DateModified and FileHash
for -WorkerPath, using Siemens' documented V21 format. TIA keeps one subkey per allowed path under the
TiaMcp.Openness.V21.exe application key ("Entry", "Entry (1)", ...), so the checkout and each worktree
keep their own entry: the subkey with the same Path is updated, otherwise a new one is added.
Exits 1 without changes when the current user cannot write there.

-Grant, run once in an elevated PowerShell: creates the application key and lets -User read, create and
set values in its subkeys. Nothing outside that key is changed. Release bundles keep using
Register-TiaOpennessApplication.ps1.
#>
param(
    [string]$WorkerPath,
    [switch]$Grant,
    [string]$User = "$env:USERDOMAIN\$env:USERNAME"
)
$ErrorActionPreference = 'Stop'
$appPath = 'SOFTWARE\Siemens\Automation\Openness\AllowList\TiaMcp.Openness.V21.exe'
$hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)

if ($Grant) {
    $key = $hklm.CreateSubKey($appPath)
    try {
        $acl = $key.GetAccessControl()
        # Inherited, so entries TIA adds itself after "Yes to all" stay updatable too.
        $acl.AddAccessRule([Security.AccessControl.RegistryAccessRule]::new($User, 'ReadKey, SetValue, CreateSubKey',
            [Security.AccessControl.InheritanceFlags]::ContainerInherit, [Security.AccessControl.PropagationFlags]::None, 'Allow'))
        $key.SetAccessControl($acl)
    } finally { $key.Dispose() }
    "Granted $User permission to maintain entries under HKLM\$appPath"
    return
}

$file = Get-Item -LiteralPath $WorkerPath
$sha = [Security.Cryptography.SHA256]::Create()
$stream = $file.OpenRead()
try { $hash = [Convert]::ToBase64String($sha.ComputeHash($stream)) } finally { $stream.Dispose(); $sha.Dispose() }
$values = [ordered]@{
    Path = $file.FullName
    DateModified = $file.LastWriteTimeUtc.ToString('yyyy/MM/dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)
    FileHash = $hash
}

$denied = "No write access under HKLM\$appPath. Run once in an elevated PowerShell: $PSCommandPath -Grant"
$readWrite = [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree
try {
    # Request only what -Grant gives; the default writable open would also ask for rights it does not have.
    $app = $hklm.OpenSubKey($appPath, $readWrite, [Security.AccessControl.RegistryRights]'ReadKey, CreateSubKey')
    if (-not $app) { Write-Warning $denied; exit 1 }
    try {
        $names = $app.GetSubKeyNames()
        $name = $names | Where-Object {
            $sub = $app.OpenSubKey($_)
            try { $sub -and [string]::Equals($sub.GetValue('Path'), $values.Path, [StringComparison]::OrdinalIgnoreCase) } finally { if ($sub) { $sub.Dispose() } }
        } | Select-Object -First 1
        if (-not $name) {
            $name = 'Entry'
            for ($i = 1; $names -contains $name; $i++) { $name = "Entry ($i)" }
            $app.CreateSubKey($name).Dispose()
        }
        $key = $app.OpenSubKey($name, $readWrite, [Security.AccessControl.RegistryRights]'SetValue, QueryValues')
    } finally { $app.Dispose() }
} catch [Security.SecurityException], [UnauthorizedAccessException] { Write-Warning $denied; exit 1 }

try {
    foreach ($n in $values.Keys) { $key.SetValue($n, $values[$n], [Microsoft.Win32.RegistryValueKind]::String) }
    foreach ($n in $values.Keys) {
        if ($key.GetValue($n) -ne $values[$n]) { throw "AllowList readback mismatch for $n." }
    }
} finally { $key.Dispose() }
"Registered $($values.Path) in the Openness AllowList ($name)"
