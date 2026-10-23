param(
    [Parameter(Mandatory=$true)][string]$Profile,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$TiaApiDirectory = 'C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) { throw 'Choose a fresh output directory; prior native evidence is retained.' }
$profilePath = (Resolve-Path -LiteralPath $Profile).Path
$source = Join-Path $repoRoot 'fixtures/libraries/NativeHmiProbe.cs'
$buildRoot = Join-Path $repoRoot ('artifacts/library-hmi-probe-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildRoot | Out-Null
$executable = Join-Path $buildRoot 'NativeHmiProbe.exe'
$sourceSnapshot = Join-Path $buildRoot 'NativeHmiProbe.cs'
Copy-Item -LiteralPath $source -Destination $sourceSnapshot
Copy-Item -LiteralPath $profilePath -Destination (Join-Path $buildRoot 'profile.json')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$references = @('Siemens.Engineering.Base.dll','Siemens.Engineering.WinCC.dll','Siemens.Engineering.WinCCUnified.dll') | ForEach-Object { '/reference:' + (Join-Path $TiaApiDirectory $_) }
& $compiler /nologo /target:exe /platform:x64 /reference:System.Web.Extensions.dll /reference:System.Core.dll /reference:System.Drawing.dll $references ("/out:" + $executable) $sourceSnapshot
if ($LASTEXITCODE -ne 0) { throw 'Native HMI probe did not compile against the installed API.' }
& $executable $TiaApiDirectory $outputPath $profilePath
if ($LASTEXITCODE -ne 0) { throw ('Native HMI probe failed. Evidence: ' + (Join-Path $outputPath 'qualification.json')) }
