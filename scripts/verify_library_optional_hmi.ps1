param(
    [Parameter(Mandatory=$true)][string]$Worker,
    [string]$TiaApiDirectory = 'C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$buildRoot = Join-Path $repoRoot ('artifacts/optional-hmi-isolation-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildRoot | Out-Null
$executable = Join-Path $buildRoot 'OptionalHmiIsolationProbe.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:exe /platform:x64 /reference:System.Core.dll ("/out:"+$executable) (Join-Path $repoRoot 'fixtures/libraries/OptionalHmiIsolationProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Optional-module isolation probe failed to compile.' }
# No vendor DLL is copied. The resolver supplies core installed modules while explicitly refusing every HMI module.
& $executable (Resolve-Path -LiteralPath $Worker).Path $TiaApiDirectory (Join-Path $buildRoot 'missing-api') $buildRoot
if ($LASTEXITCODE -ne 0) { throw 'Optional-module isolation failed; no native TIA startup was attempted.' }
Write-Output ("Evidence: " + $buildRoot)
