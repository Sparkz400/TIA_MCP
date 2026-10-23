param(
    [string]$ApiDirectory = 'C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48',
    [string]$MetadataLoadContextPath,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath (Join-Path $ApiDirectory 'net48')) { $ApiDirectory = Join-Path $ApiDirectory 'net48' }
if (-not $MetadataLoadContextPath) {
    $sdkRoot = Join-Path $env:ProgramFiles 'dotnet/sdk'
    $MetadataLoadContextPath = Get-ChildItem -LiteralPath $sdkRoot -Filter System.Reflection.MetadataLoadContext.dll -Recurse |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $MetadataLoadContextPath) { throw 'System.Reflection.MetadataLoadContext is required. No execution-load fallback is allowed.' }
Add-Type -Path $MetadataLoadContextPath
$referenceRoot = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$paths = @(Get-ChildItem -LiteralPath $ApiDirectory -Filter '*.dll' | ForEach-Object FullName) +
    @(Get-ChildItem -LiteralPath $referenceRoot -Filter '*.dll' | ForEach-Object FullName)
$context = [System.Reflection.MetadataLoadContext]::new([System.Reflection.PathAssemblyResolver]::new([string[]]$paths), 'mscorlib')
try {
    $assemblies = @()
    foreach ($name in @('Siemens.Engineering.Step7','Siemens.Engineering.Base','Siemens.Engineering.WinCCUnified')) {
        $path = Join-Path $ApiDirectory ($name + '.dll')
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $before = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
        $assembly = $context.LoadFromAssemblyPath([IO.Path]::GetFullPath($path))
        $types = @($assembly.GetExportedTypes() | Where-Object {
            $_.FullName -match '^Siemens\.Engineering\.SW\.OpcUa\.|^Siemens\.Engineering\.HW\.(OpcUa|Certificate)|^Siemens\.Engineering\.HW\.Features\.(OpcUaUserManagement|CertificateManagementConfiguration)|^Siemens\.Engineering\.HW\.Utilities\.OpcUaExportProvider|^Siemens\.Engineering\.Security\.(Certificate|LocalCertificate|SubjectAlternative|SignatureAlgorithm)|^Siemens\.Engineering\.HmiUnified\.(RuntimeSettings\.HmiOpcUa|HmiOpcUaAlarm\.)'
        } | Sort-Object FullName | ForEach-Object {
            $type = $_
            @{name=$type.FullName; properties=@($type.GetProperties() | Sort-Object Name | ForEach-Object {
                @{name=$_.Name;type=$_.PropertyType.FullName;read=($null -ne $_.GetGetMethod());write=($null -ne $_.GetSetMethod())}
            }); methods=@($type.GetMethods() | Where-Object {
                $_.DeclaringType -eq $type -and $_.IsPublic -and -not $_.IsSpecialName -and
                $_.Name -notmatch '^(GetAttribute|SetAttribute|Equals|GetHashCode|ToString|GetEnumerator|CopyTo|Contains|IndexOf)'
            } | Sort-Object Name | ForEach-Object { $_.ToString() })}
        })
        if ((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $before) { throw "Assembly changed during inspection: $name" }
        $assemblies += @{name=$name;sha256=$before;version=$assembly.GetName().Version.ToString();types=$types}
    }
    $json = @{schemaVersion=1;inspectionMode='metadataOnly';nativeCodeExecuted=$false;targetProbed=$false;
        observedAtUtc=[DateTime]::UtcNow.ToString('O');assemblies=$assemblies} | ConvertTo-Json -Depth 12
    if ($ReportPath) {
        $full = [IO.Path]::GetFullPath($ReportPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full)) | Out-Null
        $stream = [IO.File]::Open($full, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json); $stream.Write($bytes,0,$bytes.Length) }
        finally { $stream.Dispose() }
        Write-Output $full
    } else { Write-Output $json }
} finally { $context.Dispose() }
