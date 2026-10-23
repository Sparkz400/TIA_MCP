[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($OutputDirectory)
$ready=Get-Content -LiteralPath (Join-Path $root 'ui-ready.json') -Raw | ConvertFrom-Json
$admission=Get-Content -LiteralPath (Join-Path $root 'admission.json') -Raw | ConvertFrom-Json
if ($ready.processId -le 0 -or $admission.mode -ne 'author-ui') { throw 'Exact authoring-ready owned helper required.' }
$target=Join-Path $root 'coordinator-command.json'
$temporary=Join-Path $root ('coordinator-command.'+[guid]::NewGuid().ToString('N')+'.tmp')
if (Test-Path -LiteralPath $target) { throw 'A capture command already exists; no replay.' }
$bytes=[Text.UTF8Encoding]::new($false).GetBytes((@{command='capture-close';oracleSha256=$admission.oracle.sha256} | ConvertTo-Json))
$file=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
try { $file.Write($bytes,0,$bytes.Length);$file.Flush($true) } finally { $file.Dispose() }
[IO.File]::Move($temporary,$target)
[ordered]@{commandPath=$target;nativeInvoked=$false;uiSaveMustAlreadyBeComplete=$true} | ConvertTo-Json
