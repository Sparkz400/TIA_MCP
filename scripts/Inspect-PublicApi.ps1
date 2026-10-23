param(
    [string]$ApiDirectory = 'C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48',
    [Parameter(Mandatory = $true)][string]$ReportPath,
    [string]$MetadataLoadContextPath,
    [int]$MaximumAssemblies = 256,
    [int]$MaximumTypes = 100000,
    [int]$MaximumMembers = 2000000
)
$ErrorActionPreference = 'Stop'
if ($MaximumAssemblies -lt 1 -or $MaximumAssemblies -gt 256 -or
    $MaximumTypes -lt 1 -or $MaximumTypes -gt 100000 -or
    $MaximumMembers -lt 1 -or $MaximumMembers -gt 2000000) { throw 'Inventory limits outside permitted bounds.' }
$apiRoot = [IO.Path]::GetFullPath($ApiDirectory)
if (Test-Path -LiteralPath (Join-Path $apiRoot 'net48')) { $apiRoot = Join-Path $apiRoot 'net48' }
if (-not (Test-Path -LiteralPath $apiRoot -PathType Container)) { throw 'Public API directory does not exist.' }
$reportFull = [IO.Path]::GetFullPath($ReportPath)
if (Test-Path -LiteralPath $reportFull) { throw 'Report already exists; metadata inventories are immutable.' }
if (-not $MetadataLoadContextPath) {
    $MetadataLoadContextPath = Get-ChildItem -LiteralPath (Join-Path $env:ProgramFiles 'dotnet/sdk') -Filter System.Reflection.MetadataLoadContext.dll -Recurse |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $MetadataLoadContextPath) { throw 'MetadataLoadContext is required; no execution-load fallback is allowed.' }
$apiFiles = @(Get-ChildItem -LiteralPath $apiRoot -Filter '*.dll' -File | Sort-Object Name)
if ($apiFiles.Count -eq 0 -or $apiFiles.Count -gt $MaximumAssemblies) { throw 'Public API assembly count outside configured bound.' }
foreach ($file in $apiFiles) { if ($file.Length -gt 268435456) { throw "Assembly exceeds 256 MiB: $($file.Name)" } }
Add-Type -Path $MetadataLoadContextPath
$helperPath = Join-Path $PSScriptRoot 'public_api_metadata/MetadataInspector.cs'
$scriptHash = (Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant()
$helperHash = (Get-FileHash -LiteralPath $helperPath).Hash.ToLowerInvariant()
if (-not ('TiaMcp.PublicApiMetadata.Inspector' -as [type])) {
    Add-Type -Path $helperPath
    [AppDomain]::CurrentDomain.SetData('TiaMcp.PublicApiMetadata.HelperSha256', $helperHash)
} elseif ([AppDomain]::CurrentDomain.GetData('TiaMcp.PublicApiMetadata.HelperSha256') -ne $helperHash) {
    throw 'A different metadata helper is already loaded; use a fresh PowerShell process.'
}
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$resolverPaths = @($apiFiles | ForEach-Object FullName) + @(Get-ChildItem -LiteralPath $frameworkRoot -Filter '*.dll' -File | ForEach-Object FullName)
$context = [System.Reflection.MetadataLoadContext]::new([System.Reflection.PathAssemblyResolver]::new([string[]]$resolverPaths), 'mscorlib')
try {
    $assemblies = @()
    $totalTypes = 0
    $totalMembers = 0
    foreach ($file in $apiFiles) {
        $before = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $xmlPath = [IO.Path]::ChangeExtension($file.FullName, '.xml')
        $xmlHash = if (Test-Path -LiteralPath $xmlPath) { (Get-FileHash -LiteralPath $xmlPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        $assembly = $context.LoadFromAssemblyPath($file.FullName)
        $result = [TiaMcp.PublicApiMetadata.Inspector]::Inspect($assembly, $MaximumTypes - $totalTypes, $MaximumMembers - $totalMembers)
        $documentationIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        if ($xmlHash) {
            $xmlSettings = [Xml.XmlReaderSettings]::new()
            $xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $xmlSettings.XmlResolver = $null
            $xmlSettings.MaxCharactersInDocument = 67108864
            $reader = [Xml.XmlReader]::Create($xmlPath, $xmlSettings)
            try { while ($reader.Read()) { if ($reader.NodeType -eq [Xml.XmlNodeType]::Element -and $reader.Name -eq 'member') { [void]$documentationIds.Add($reader.GetAttribute('name')) } } }
            finally { $reader.Dispose() }
        }
        $totalTypes += $result.Types.Count
        foreach ($type in $result.Types) {
            $totalMembers += $type.members.Count
            foreach ($member in $type.members) {
                $member['xmlDocumentationPresent'] = $documentationIds.Contains($member['id'])
                $member['xmlDocumentationScope'] = $assembly.GetName().Name
            }
        }
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $before) { throw "Assembly changed during inspection: $($file.Name)" }
        if ($xmlHash -and (Get-FileHash -LiteralPath $xmlPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $xmlHash) { throw "XML reference changed during inspection: $($file.Name)" }
        $assemblies += [ordered]@{ name=$assembly.GetName().Name; path=$file.FullName; sha256=$before;
            version=$assembly.GetName().Version.ToString(); xmlPath=$(if ($xmlHash) {$xmlPath} else {$null}); xmlSha256=$xmlHash;
            types=$result.Types; errors=$result.Errors }
    }
    if ((Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant() -ne $scriptHash -or
        (Get-FileHash -LiteralPath $helperPath).Hash.ToLowerInvariant() -ne $helperHash) { throw 'Inspector source changed during inventory; no report published.' }
    # Publication is create-new; no Siemens object has been constructed or invoked.
    $report = [ordered]@{ schemaVersion=1; inspectionMode='metadataOnly'; nativeCodeExecuted=$false; targetProbed=$false; memberScope='declaredPublic';
        observedAtUtc=[DateTime]::UtcNow.ToString('O'); apiDirectory=$apiRoot;
        producer=[ordered]@{ scriptSha256=$scriptHash;
            helperSha256=$helperHash;
            metadataLoadContextPath=$MetadataLoadContextPath; metadataLoadContextSha256=(Get-FileHash -LiteralPath $MetadataLoadContextPath).Hash.ToLowerInvariant() };
        limits=[ordered]@{maximumAssemblies=$MaximumAssemblies;maximumTypes=$MaximumTypes;maximumMembers=$MaximumMembers};
        assemblies=$assemblies; typeCount=$totalTypes; memberCount=$totalMembers }
    $json = $report | ConvertTo-Json -Depth 60 -Compress
    if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 134217728) { throw 'Metadata report exceeds 128 MiB consumer bound; no report published.' }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportFull)) | Out-Null
    $stream = [IO.File]::Open($reportFull, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $bytes=[Text.UTF8Encoding]::new($false).GetBytes($json); $stream.Write($bytes,0,$bytes.Length) }
    finally { $stream.Dispose() }
    [ordered]@{reportPath=$reportFull;assemblies=$assemblies.Count;types=$totalTypes;members=$totalMembers;
        sha256=(Get-FileHash -LiteralPath $reportFull -Algorithm SHA256).Hash.ToLowerInvariant()} | ConvertTo-Json -Compress
} finally { $context.Dispose() }

