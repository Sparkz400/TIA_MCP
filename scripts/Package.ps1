param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [string]$IsolatedBuildDirectory,
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$bundleKind = if ($Offline) { 'offline' } else { 'engineering' }
if ($SkipBuild -and -not $IsolatedBuildDirectory) { throw '-SkipBuild requires -IsolatedBuildDirectory from New-IsolatedBuild.ps1; shared bin directories are not release inputs.' }
if (-not $IsolatedBuildDirectory) { $IsolatedBuildDirectory = & (Join-Path $PSScriptRoot 'New-IsolatedBuild.ps1') -Configuration $Configuration -Offline:$Offline }
$buildRoot = (Resolve-Path -LiteralPath $IsolatedBuildDirectory).Path
$inputs = Get-Content -LiteralPath (Join-Path $buildRoot 'build-inputs.json') -Raw | ConvertFrom-Json
$outputs = Get-Content -LiteralPath (Join-Path $buildRoot 'build-outputs.json') -Raw | ConvertFrom-Json
foreach ($record in @($inputs,$outputs)) {
    $recordKind = if ($record.bundleKind) { $record.bundleKind } else { 'engineering' }
    if ($recordKind -cne $bundleKind) { throw 'Build/package kinds differ. Use -Offline only with an isolated offline build.' }
}
if ($inputs.configuration -ne $Configuration -or $outputs.configuration -ne $Configuration) { throw 'Build/package configurations differ.' }
if ((Get-FileHash -LiteralPath (Join-Path $buildRoot 'build-inputs.json')).Hash -ine $outputs.inputManifestSha256) { throw 'Build input manifest changed after compilation.' }
foreach ($entry in @($inputs.files) + @($outputs.files)) {
    if ($entry.path -match '(^[\\/]|:|(^|[\\/])\.\.([\\/]|$))') { throw 'Unsafe build manifest path.' }
    $file = Join-Path $buildRoot $entry.path
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $entry.sha256) { throw "Build input/output changed: $($entry.path)" }
}
# The product version (Directory.Build.props) as compiled into the Host; Verify-TiaMcpBundle.ps1 checks the manifest against it.
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $buildRoot 'published-host/TiaMcp.Host.dll')).ProductVersion
if (-not $productVersion) { throw 'The published Host carries no product version.' }
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss') + '_' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$packageName = if ($Offline) { 'tia-mcp-offline' } else { 'tia-mcp-v21' }
$bundleRoot = Join-Path $repoRoot "artifacts/releases/${packageName}-${productVersion}_$stamp"
New-Item -ItemType Directory -Path $bundleRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $buildRoot 'published-host') -Destination (Join-Path $bundleRoot 'host') -Recurse
foreach ($worker in $(if ($Offline) { @() } else { @(@{name='openness';project='TiaMcp.Openness.V21'}, @{name='simulator';project='TiaMcp.Simulator.V8'}) })) {
    $destination = Join-Path $bundleRoot ('workers/' + $worker.name)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $buildRoot ('src/' + $worker.project + '/bin/' + $Configuration + '/net48')) -File |
        Where-Object { $_.Extension -in '.exe','.dll','.config','.json' } | Copy-Item -Destination $destination
}
foreach ($folder in @('scripts','fixtures','docs','contracts','third-party','ui')) {
    Copy-Item -LiteralPath (Join-Path $buildRoot $folder) -Destination $bundleRoot -Recurse
}
foreach ($name in @('README.md','LICENSE','NOTICE','CONTRIBUTING.md','SECURITY.md','build-inputs.json','build-outputs.json')) { Copy-Item -LiteralPath (Join-Path $buildRoot $name) -Destination $bundleRoot }
$dependencies = Get-Content -LiteralPath (Join-Path $bundleRoot 'host/TiaMcp.Host.deps.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (@($dependencies.libraries.PSObject.Properties | Where-Object { $_.Name -like 'OPCFoundation.NetStandard.Opc.Ua.Server/*' }).Count -or
    (Test-Path -LiteralPath (Join-Path $bundleRoot 'host/Opc.Ua.Server.dll'))) {
    throw 'The OPC UA qualification server must not be redistributed as a Host runtime dependency.'
}
$licenseMap = @{
    'System.Security.Cryptography.ProtectedData' = @('system.security.cryptography.protecteddata.nuspec','System.Security.Cryptography.ProtectedData.LICENSE.txt','System.Security.Cryptography.ProtectedData.NOTICES.txt')
    'ModelContextProtocol' = @('modelcontextprotocol.nuspec','ModelContextProtocol.LICENSE','ModelContextProtocol.NOTICES.txt')
    'ModelContextProtocol.Core' = @('modelcontextprotocol.core.nuspec','ModelContextProtocol.LICENSE','ModelContextProtocol.NOTICES.txt')
    'Microsoft.Extensions.AI.Abstractions' = @('microsoft.extensions.ai.abstractions.nuspec','Microsoft.Extensions.LICENSE.txt','Microsoft.Extensions.NOTICES.txt')
    'Newtonsoft.Json' = @('opcua-runtime/newtonsoft.json.nuspec','opcua-runtime/Newtonsoft.Json.LICENSE.md')
    'BitFaster.Caching' = @('opcua-runtime/bitfaster.caching.nuspec','opcua-runtime/BitFaster.Caching.LICENSE')
    'OPCFoundation.NetStandard.Opc.Ua.Client' = @('opcua-runtime/opcfoundation.netstandard.opc.ua.client.nuspec','opcua-runtime/OPCFoundation.LICENSE.txt')
    'OPCFoundation.NetStandard.Opc.Ua.Configuration' = @('opcua-runtime/opcfoundation.netstandard.opc.ua.configuration.nuspec','opcua-runtime/OPCFoundation.LICENSE.txt')
    'OPCFoundation.NetStandard.Opc.Ua.Core' = @('opcua-runtime/opcfoundation.netstandard.opc.ua.core.nuspec','opcua-runtime/OPCFoundation.LICENSE.txt')
    'OPCFoundation.NetStandard.Opc.Ua.Security.Certificates' = @('opcua-runtime/opcfoundation.netstandard.opc.ua.security.certificates.nuspec','opcua-runtime/OPCFoundation.LICENSE.txt')
    'OPCFoundation.NetStandard.Opc.Ua.Types' = @('opcua-runtime/opcfoundation.netstandard.opc.ua.types.nuspec','opcua-runtime/OPCFoundation.LICENSE.txt')
}
$hostPackages = @($dependencies.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object {
    $identity = $_.Name -split '/'
    $notices = $licenseMap[$identity[0]]
    if (-not $notices -or $identity.Count -ne 2) { throw ('Missing retained license mapping for Host dependency: ' + $_.Name) }
    [xml]$packageMetadata = Get-Content -LiteralPath (Join-Path $bundleRoot ('third-party/' + $notices[0])) -Raw -Encoding UTF8
    if ($packageMetadata.package.metadata.id -cne $identity[0] -or $packageMetadata.package.metadata.version -cne $identity[1]) { throw ('Retained license metadata version differs: ' + $_.Name) }
    @{id=$identity[0];version=$identity[1];license=$packageMetadata.package.metadata.license.InnerText;files=@($notices | ForEach-Object {
        $relative = 'third-party/' + $_
        @{path=$relative;sha256=(Get-FileHash -LiteralPath (Join-Path $bundleRoot $relative) -Algorithm SHA256).Hash.ToLowerInvariant()}
    })}
})
@{schemaVersion=1;scope='Published Host NuGet dependencies; shared framework and separately installed Git excluded';packages=$hostPackages} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $bundleRoot 'third-party/host-packages.json') -Encoding UTF8
if (Get-ChildItem -LiteralPath $bundleRoot -Recurse -File -Filter 'Siemens.*.dll') { throw 'Unexpected redistributed Siemens DLL: package rejected.' }
$files = @(Get-ChildItem -LiteralPath $bundleRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    @{path=$_.FullName.Substring($bundleRoot.Length + 1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
@{schemaVersion=1;createdAtUtc=[DateTime]::UtcNow.ToString('O');productVersion=$productVersion;frameworkDependent=$true;configuration=$Configuration;bundleKind=$bundleKind;
    sourceInputManifestSha256=$outputs.inputManifestSha256;files=$files} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bundleRoot 'manifest.json') -Encoding UTF8
& (Join-Path $bundleRoot 'scripts/Verify-TiaMcpBundle.ps1') -BundlePath $bundleRoot -Strict | Out-Host
Compress-Archive -Path "$bundleRoot/*" -DestinationPath ($bundleRoot + '.zip') -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath ($bundleRoot + '.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
($zipHash + ' *' + (Split-Path $bundleRoot -Leaf) + '.zip') | Set-Content -LiteralPath ($bundleRoot + '.zip.sha256') -Encoding UTF8
# Machine-specific configuration stays outside the immutable bundle.
& (Join-Path $bundleRoot 'scripts/New-McpConfiguration.ps1') -BundlePath $bundleRoot -StrictBundle |
    Set-Content -LiteralPath ($bundleRoot + '.mcp.json') -Encoding UTF8
Write-Output $bundleRoot
