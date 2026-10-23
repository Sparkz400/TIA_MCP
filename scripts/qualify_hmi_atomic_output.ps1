param([Parameter(Mandatory=$true)][string]$Helper, [Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
$helperPath = (Resolve-Path -LiteralPath $Helper).Path
$outputPath = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $outputPath) { throw 'Qualification output must be new' }
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
# Load metadata dependencies only; never call Program.Main or start TIA.
$apiDirectory = 'C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48'
[Reflection.Assembly]::LoadFrom((Join-Path (Split-Path -Parent $helperPath) 'Newtonsoft.Json.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $apiDirectory 'Siemens.Engineering.Base.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $apiDirectory 'Siemens.Engineering.Step7.dll')) | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom($helperPath)
$method = $assembly.GetType('Program', $true).GetMethod('CreateDirectoryW', [Reflection.BindingFlags]'NonPublic,Static')
if ($null -eq $method) { throw 'Exact compiled CreateDirectoryW entry point missing' }
$attribute = $method.GetCustomAttributes([Runtime.InteropServices.DllImportAttribute], $false)[0]
if ($attribute.EntryPoint -ne 'CreateDirectoryW' -or -not $attribute.ExactSpelling) { throw 'Exact Unicode Win32 entry required' }
$create = $method.CreateDelegate([Func[string,IntPtr,bool]])
$target = Join-Path $outputPath 'owned-Ünicode-新'
$first = $create.Invoke($target, [IntPtr]::Zero)
if ($first -ne $true -or -not [IO.Directory]::Exists($target)) { throw 'Atomic creation failed' }
$sentinel = Join-Path $target 'sentinel.txt'
[IO.File]::WriteAllText($sentinel, 'original owned bytes')
$second = $create.Invoke($target, [IntPtr]::Zero)
if ($second -ne $false -or [IO.File]::ReadAllText($sentinel) -ne 'original owned bytes') { throw 'Existing output was not refused unchanged' }
$receipt = [ordered]@{completed=$true; nativeDispatched=$false; helper=[ordered]@{path=$helperPath; sha256=(Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash.ToLowerInvariant()}; exactUnicodeEntry=$true; firstCreationSucceeded=$first; existingDirectoryRefused=(-not $second); sentinelUnchanged=$true; processRuntime=[Environment]::Version.ToString()}
$receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputPath 'result.json') -Encoding UTF8
$receipt | ConvertTo-Json -Depth 4
