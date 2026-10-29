param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Select a fresh sandbox validation directory.' }
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$sdk = & $dotnet --version
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the current .NET SDK.' }
$compiler = Join-Path (Split-Path $dotnet -Parent) "sdk/$sdk/Roslyn/bincore/csc.dll"
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$references = @('mscorlib.dll','System.dll','System.Core.dll') | ForEach-Object { Join-Path $framework $_ }
foreach ($path in @($compiler) + $references) { if (!(Test-Path -LiteralPath $path)) { throw "Required compiler/runtime unavailable: $path" } }
New-Item -ItemType Directory -Path $output | Out-Null
# The companion's TIA-free sources; SelectionHandoff.cs needs the Siemens API and stays out.
$sources = @('src/TiaMcp.AddIn.V21/HandoffJson.cs', 'src/TiaMcp.AddIn.V21/HostPipe.cs', 'fixtures/addin-sandbox/SandboxFixture.cs') | ForEach-Object { Join-Path $repoRoot $_ }
$arguments = @('/nologo','/noconfig','/nostdlib+','/target:exe','/platform:x64','/langversion:latest',('/out:' + (Join-Path $output 'sandbox-fixture.exe')))
$arguments += $references | ForEach-Object { '/reference:' + $_ }
$arguments += $sources
& $dotnet $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Add-In sandbox fixture compilation failed.' }
& (Join-Path $output 'sandbox-fixture.exe') (Join-Path $output 'result.json')
if ($LASTEXITCODE -ne 0) { throw 'Add-In sandbox fixture execution failed.' }
