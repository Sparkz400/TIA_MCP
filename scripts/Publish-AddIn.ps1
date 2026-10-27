<#
.SYNOPSIS
Builds the TIA MCP Add-In, validates its Publisher configuration against the installed XSD and runs the Siemens Publisher.

.DESCRIPTION
Writes TiaMcp.AddIn.V21.addin, publisher.log and addin-enrollment.json into a new output directory.
Pass the enrollment file to the Host with --addin-handoff --addin-enrollment <file>.
The script never installs the Add-In into TIA Portal and never starts TIA; the Publisher is a standalone tool.
#>
#Requires -Version 7
param(
    [string]$TiaApiDirectory = 'C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48',
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/addin-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$apiRoot = Split-Path $TiaApiDirectory -Parent
$publisher = Join-Path $apiRoot 'Siemens.Engineering.AddIn.Publisher.exe'
$xsd = Join-Path $apiRoot 'Siemens.Engineering.AddIn.Publisher.xsd'
foreach ($path in $publisher, $xsd) { if (-not (Test-Path -LiteralPath $path)) { throw "Missing $path. Set -TiaApiDirectory to the installed PublicAPI\V21\net48 directory." } }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "$output already exists; choose a new -OutputDirectory so earlier packages stay unchanged." }
$build = Join-Path $output 'build'

dotnet build (Join-Path $repoRoot 'src/TiaMcp.AddIn.V21/TiaMcp.AddIn.V21.csproj') -c $Configuration "-p:TiaApiDirectory=$TiaApiDirectory" -o $build -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Add-In build failed with exit code $LASTEXITCODE" }
$assembly = Join-Path $build 'TiaMcp.AddIn.V21.dll'
$config = Join-Path $build 'AddIn.Publisher.xml'

$settings = [Xml.XmlReaderSettings]::new()
$settings.ValidationType = [Xml.ValidationType]::Schema
$settings.ValidationFlags = $settings.ValidationFlags -bor [Xml.Schema.XmlSchemaValidationFlags]::ReportValidationWarnings
$null = $settings.Schemas.Add('http://www.siemens.com/automation/Openness/AddIn/Publisher/V21', $xsd)
$errors = [Collections.Generic.List[string]]::new()
$settings.add_ValidationEventHandler({ param($sender, $e) $errors.Add("$($e.Severity): $($e.Message)") })
$reader = [Xml.XmlReader]::Create($config, $settings)
try { while ($reader.Read()) { } } finally { $reader.Dispose() }
if ($errors.Count) { throw "Publisher configuration does not match $xsd`n$($errors -join "`n")" }

# Keep the Publisher's engineering-member check enabled (no --skipEngMemberCheck).
$package = Join-Path $output 'TiaMcp.AddIn.V21.addin'
& $publisher --configuration $config --outfile $package --logfile (Join-Path $output 'publisher.log') --console
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $package)) { throw "Publisher failed with exit code $LASTEXITCODE; see $(Join-Path $output 'publisher.log')" }

# Same identity the Add-In declares: SHA-256 of the deterministic module version ID (TIA may load Add-Ins from bytes).
$stream = [IO.File]::OpenRead($assembly)
try { $pe = [Reflection.PortableExecutable.PEReader]::new($stream); $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe); $mvid = $metadata.GetGuid($metadata.GetModuleDefinition().Mvid) }
finally { if ($pe) { $pe.Dispose() }; $stream.Dispose() }
$enrollment = [ordered]@{
    schemaVersion = 'addin-enrollment.v1'
    assemblySha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($mvid.ToByteArray())).ToLowerInvariant()
    configurationSha256 = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $output 'addin-enrollment.json'), ($enrollment | ConvertTo-Json) + "`n")
Write-Host "Package:    $package"
Write-Host "Enrollment: $(Join-Path $output 'addin-enrollment.json')"
Write-Host 'Install the package in TIA Portal yourself; this script does not install it.'
