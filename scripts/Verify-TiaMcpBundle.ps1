param(
    [Parameter(Mandatory=$true)][string]$BundlePath,
    [switch]$Strict
)
$ErrorActionPreference = 'Stop'
function Get-BundleSha256([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
$rootItem = Get-Item -LiteralPath $BundlePath -Force
if (-not $rootItem.PSIsContainer) { throw 'BundlePath must be a directory.' }
$bundleRoot = [IO.Path]::GetFullPath($rootItem.FullName).TrimEnd('\','/')
$prefix = $bundleRoot + [IO.Path]::DirectorySeparatorChar
$ancestor = $bundleRoot
while ($ancestor) {
    if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'BundlePath must not traverse a reparse point.' }
    $parentDirectory = [IO.Directory]::GetParent($ancestor)
    $ancestor = if ($parentDirectory) { $parentDirectory.FullName } else { $null }
}
$manifestFile = Join-Path $bundleRoot 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) { throw 'Bundle manifest.json is missing.' }
# Inspect directory entries before descending: Get-ChildItem -Recurse has different
# link traversal behavior across Windows PowerShell and PowerShell versions.
$pending = New-Object 'System.Collections.Generic.Queue[string]'
$pending.Enqueue($bundleRoot)
$actualFiles = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
while ($pending.Count -gt 0) {
    foreach ($item in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Bundle contains a reparse point: ' + $item.Name) }
        if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) }
        else {
            $relative = $item.FullName.Substring($prefix.Length).Replace('\','/')
            if ($actualFiles.ContainsKey($relative)) { throw 'Bundle contains duplicate case-insensitive file paths.' }
            $actualFiles.Add($relative, $item.FullName)
            if ($item.Name -match '^Siemens.*\.(dll|exe)$') { throw ('Siemens binaries must resolve from the local licensed installation: ' + $relative) }
        }
    }
}
$manifest = Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
$bundleKind = if ($manifest.bundleKind) { $manifest.bundleKind } else { 'engineering' }
if ($bundleKind -cnotin @('engineering','offline')) { throw 'Unsupported bundle kind.' }
if (-not $manifest.files -or $manifest.files -isnot [Array]) { throw 'Manifest files must be a nonempty array.' }
$listed = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    if ($entry.path -isnot [string] -or $entry.path -match '^[\\/]' -or $entry.path -match '[:\x00-\x1f"<>|?*]' -or
        @($entry.path -split '[\\/]' | Where-Object { -not $_ -or $_ -eq '.' -or $_ -eq '..' -or $_ -match '[. ]$' }).Count -gt 0) { throw 'Manifest contains an unsafe relative path.' }
    $relative = $entry.path.Replace('\','/')
    if ($relative -eq 'manifest.json' -or -not $listed.Add($relative)) { throw 'Manifest contains a duplicate or self-referential entry.' }
    $full = [IO.Path]::GetFullPath((Join-Path $bundleRoot $relative))
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes the bundle.' }
    if (-not $actualFiles.ContainsKey($relative)) { throw ('Manifest file is missing: ' + $relative) }
    if ($entry.sha256 -isnot [string] -or $entry.sha256 -cnotmatch '^[a-fA-F0-9]{64}$' -or
        $null -eq $entry.bytes -or $entry.bytes -is [string] -or $entry.bytes -is [bool] -or $entry.bytes -lt 0 -or [decimal]$entry.bytes -ne [decimal][long]$entry.bytes) { throw 'Manifest contains an invalid hash or byte count.' }
    if ((Get-Item -LiteralPath $full).Length -ne $entry.bytes) { throw ('Bundle size mismatch: ' + $relative) }
    if ((Get-BundleSha256 $full) -ine $entry.sha256) { throw ('Bundle hash mismatch: ' + $relative) }
}
$runtimeFiles = @('host/TiaMcp.Host.exe', 'host/TiaMcp.Host.dll', 'host/TiaMcp.Host.deps.json', 'host/TiaMcp.Host.runtimeconfig.json', 'scripts/Start-TiaMcp.ps1', 'scripts/Verify-TiaMcpBundle.ps1', 'ui/project-explorer/index.html', 'ui/project-explorer/index.template.html', 'ui/project-explorer/model.js', 'ui/project-explorer/client.js', 'ui/project-explorer/explorer.js', 'ui/project-explorer/style.css', 'contracts/project-model.v1.schema.json', 'contracts/project-model-provider.v1.schema.json')
if ($bundleKind -eq 'engineering') { $runtimeFiles += @('workers/openness/TiaMcp.Openness.V21.exe', 'workers/simulator/TiaMcp.Simulator.V8.exe') }
else {
    if (@($actualFiles.Keys | Where-Object { $_ -like 'workers/*' -or $_ -match '(^|/)(TiaMcp.Openness.V21|TiaMcp.Simulator.V8)\.(exe|dll)$' }).Count) { throw 'Offline bundles cannot contain native workers.' }
    $runtimeFiles += @('LICENSE','NOTICE','third-party/host-packages.json','third-party/ModelContextProtocol.LICENSE','third-party/Microsoft.Extensions.LICENSE.txt','third-party/ModelContextProtocol.NOTICES.txt','third-party/Microsoft.Extensions.NOTICES.txt')
}
foreach ($required in $runtimeFiles) {
    if (-not $listed.Contains($required)) { throw ('Manifest omits a required runtime file: ' + $required) }
}
$extras = @($actualFiles.Keys | Where-Object { $_ -ne 'manifest.json' -and -not $listed.Contains($_) })
foreach ($required in @('contracts/typed-graphical-model.schema.json', 'contracts/typed-graphical-change.schema.json', 'contracts/typed-graphical-read.schema.json', 'contracts/typed-graphical-profiles.schema.json', 'fixtures/typed-graphical/profiles.v1.json', 'fixtures/typed-graphical/instance-effects.v1.json')) {
    if (-not $listed.Contains($required)) { throw ('Manifest omits a required typed graphical resource: ' + $required) }
}
foreach ($extra in $extras) {
    if ($Strict -or [IO.Path]::GetExtension($extra) -match '^\.(exe|dll|ps1|psm1|psd1|bat|cmd|com|vbs|vbe|js|jse|wsf|wsh|py|pyc|pyo|pyd|msi|sys|json|config)$') { throw ('Bundle contains an unlisted file: ' + $extra) }
}
[pscustomobject]@{
    verified = $true
    bundlePath = $bundleRoot
    bundleKind = $bundleKind
    fileCount = $listed.Count
    strict = [bool]$Strict
    extraNonRuntimeFiles = $extras
    manifestSha256 = Get-BundleSha256 $manifestFile
    trust = 'Hash consistency only; the manifest is not a publisher signature.'
}
