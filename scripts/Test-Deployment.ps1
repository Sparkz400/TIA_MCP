$ErrorActionPreference = 'Stop'
$generator = Join-Path $PSScriptRoot 'New-McpConfiguration.ps1'
$verifier = Join-Path $PSScriptRoot 'Verify-TiaMcpBundle.ps1'
$checks = 0
function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Assert-Rejected([scriptblock]$Action, [string]$Message, [string]$ExpectedReason) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = -not $ExpectedReason -or $_.Exception.Message -match $ExpectedReason }
    Assert-Check $rejected $Message
}
$local = (& $generator -BundlePath 'C:\TIA MCP' -ArtifactsDirectory 'D:\Evidence' -TiaApiDirectory 'C:\TIA API' -SimulatorApiDirectory 'C:\SIM API' -GitExecutable 'C:\Program Files\Git\cmd\git.exe' -TimeoutSeconds 73 -StrictBundle) | ConvertFrom-Json
Assert-Check ($local.mcpServers.'tia-v21'.command -eq 'powershell.exe' -and $local.mcpServers.'tia-v21'.args -contains 'C:\TIA MCP\scripts\Start-TiaMcp.ps1') 'Local configuration lost its absolute path with spaces.'
foreach ($value in @('-ArtifactsDirectory','D:\Evidence','-TiaApiDirectory','C:\TIA API','-SimulatorApiDirectory','C:\SIM API','-GitExecutable','C:\Program Files\Git\cmd\git.exe','-TimeoutSeconds','73','-StrictBundle')) {
    Assert-Check ($local.mcpServers.'tia-v21'.args -contains $value) ('Local arguments lost: ' + $value)
}
$hostileButValid = 'C:\TIA '' & $(literal) %PATH%; name'
$remote = (& $generator -BundlePath $hostileButValid -SshHost 'engineer@tia-vm' -ArtifactsDirectory 'D:\Evidence Folder' -TiaApiDirectory 'C:\TIA API' -SimulatorApiDirectory 'C:\SIM API' -GitExecutable ($hostileButValid + '\git.exe') -ConnectTimeoutSeconds 19 -TimeoutSeconds 73 -StrictBundle) | ConvertFrom-Json
Assert-Check ($remote.mcpServers.'tia-v21'.command -eq 'ssh.exe') 'Remote configuration does not use OpenSSH.'
foreach ($argument in @('StrictHostKeyChecking=yes','BatchMode=yes','ConnectTimeout=19','ConnectionAttempts=1','ServerAliveInterval=15','ServerAliveCountMax=3','-T','--','engineer@tia-vm')) {
    Assert-Check ($remote.mcpServers.'tia-v21'.args -contains $argument) ('Remote configuration lost SSH control: ' + $argument)
}
$command = $remote.mcpServers.'tia-v21'.args[-1]
Assert-Check ($command -match '^powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -EncodedCommand [a-zA-Z0-9+/=]+$') 'Remote command includes unencoded caller input.'
$decoded = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String(($command -split ' ')[-1]))
$parseErrors = $null
$tokens = $null
$tree = [Management.Automation.Language.Parser]::ParseInput($decoded, [ref]$tokens, [ref]$parseErrors)
Assert-Check ($parseErrors.Count -eq 0) 'Encoded remote command is invalid PowerShell.'
$commands = @($tree.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true))
Assert-Check ($commands.Count -eq 1) 'Caller path injected another remote command.'
Assert-Check ($commands[0].CommandElements[0].Value -ceq ($hostileButValid + '\scripts\Start-TiaMcp.ps1')) 'Decoded launcher path changed during shell quoting.'
$literalValues = @($commands[0].CommandElements | Where-Object { $_ -is [Management.Automation.Language.StringConstantExpressionAst] } | ForEach-Object { $_.Value })
foreach ($value in @('D:\Evidence Folder','C:\TIA API','C:\SIM API','73',($hostileButValid + '\git.exe'))) { Assert-Check ($literalValues -contains $value) ('Remote override changed: ' + $value) }
foreach ($path in @('relative/path','C:relative','\root-relative','\\?\C:\device-path','C:\bad"path',"C:\bad`npath",'C:\x\..\y','C:\file:stream','C:\trailing.','C:\bad*path')) {
    Assert-Rejected { & $generator -BundlePath $path } ('Unsafe or relative bundle path accepted: ' + $path)
    Assert-Rejected { & $generator -BundlePath 'C:\TiaMcp' -ArtifactsDirectory $path } 'Unsafe artifact path accepted.'
    Assert-Rejected { & $generator -BundlePath 'C:\TiaMcp' -TiaApiDirectory $path } 'Unsafe API path accepted.'
    Assert-Rejected { & $generator -BundlePath 'C:\TiaMcp' -GitExecutable $path } 'Unsafe Git executable path accepted.'
}
foreach ($alias in @('-oProxyCommand=test','host;command',"host`ncommand",'a@b@c','a@','@host')) { Assert-Rejected { & $generator -BundlePath 'C:\TiaMcp' -SshHost $alias } 'Invalid SSH destination accepted.' }
Assert-Rejected { & $generator -BundlePath 'C:\TiaMcp' -TimeoutSeconds 901 } 'Out-of-range bridge timeout accepted.'
Assert-Rejected { & $generator -BundlePath 'C:\TiaMcp' -ConnectTimeoutSeconds 0 } 'Out-of-range SSH timeout accepted.'
Assert-Check ($local.mcpServers.'tia-v21'.args -notcontains '-InteractiveSession') 'Direct mode silently enabled an interactive session.'
$interactive = (& $generator -BundlePath 'C:\TIA MCP' -SshHost 'tia-vm' -SessionMode InteractiveUser -StrictBundle) | ConvertFrom-Json
$interactiveCommand = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String(($interactive.mcpServers.'tia-v21'.args[-1] -split ' ')[-1]))
Assert-Check ($interactiveCommand -match ' -InteractiveSession(?:;|$)') 'Explicit interactive mode was lost in the SSH command.'
Assert-Rejected { & $generator -BundlePath 'C:\TiaMcp' -SessionMode Auto } 'An implicit fallback mode was accepted.'

$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$testRoot = Join-Path $repoRoot ('artifacts/deployment-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$required = @('host/TiaMcp.Host.exe','host/TiaMcp.Host.dll','host/TiaMcp.Host.deps.json','host/TiaMcp.Host.runtimeconfig.json','workers/openness/TiaMcp.Openness.V21.exe','workers/simulator/TiaMcp.Simulator.V8.exe','scripts/Start-TiaMcp.ps1','scripts/Verify-TiaMcpBundle.ps1','ui/project-explorer/index.html','ui/project-explorer/index.template.html','ui/project-explorer/model.js','ui/project-explorer/client.js','ui/project-explorer/explorer.js','ui/project-explorer/style.css','contracts/project-model.v1.schema.json','contracts/project-model-provider.v1.schema.json')
$required += @('contracts/typed-graphical-model.schema.json', 'contracts/typed-graphical-change.schema.json', 'contracts/typed-graphical-read.schema.json', 'contracts/typed-graphical-profiles.schema.json', 'fixtures/typed-graphical/profiles.v1.json', 'fixtures/typed-graphical/instance-effects.v1.json')
foreach ($typedResource in $required | Where-Object { $_ -like '*typed-graphical*' }) {
    Assert-Check (Test-Path -LiteralPath (Join-Path $repoRoot $typedResource) -PathType Leaf) ('Required typed resource is absent from the actual source bundle: ' + $typedResource)
}
foreach ($name in $required) {
    $target = Join-Path $testRoot $name
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($target, 'test fixture only, never execute')
}
$manifestPath = Join-Path $testRoot 'manifest.json'
$entries = @($required | ForEach-Object { $file = Get-Item -LiteralPath (Join-Path $testRoot $_); @{path=$_; bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} })
function Write-TestManifest($Rows) { @{files=@($Rows)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8 }
Write-TestManifest $entries
Assert-Check ((& $verifier -BundlePath $testRoot -Strict).verified) 'Valid bundle failed strict verification.'
foreach ($typedResource in @('contracts/typed-graphical-model.schema.json', 'fixtures/typed-graphical/profiles.v1.json', 'fixtures/typed-graphical/instance-effects.v1.json')) {
    Write-TestManifest @($entries | Where-Object { $_.path -ne $typedResource })
    Assert-Rejected { & $verifier -BundlePath $testRoot } ('Typed graphical resource omission accepted: ' + $typedResource) 'required typed graphical resource'
}
Write-TestManifest $entries
foreach ($bad in @('../escape.dll','/absolute.dll','C:\absolute.dll','host//empty.dll','host/./dot.dll','host/a:stream','host/trailing.')) {
    Write-TestManifest (@($entries) + @{path=$bad; bytes=0; sha256=('0' * 64)})
    Assert-Rejected { & $verifier -BundlePath $testRoot } 'Unsafe manifest path accepted.'
}
Write-TestManifest (@($entries) + @{path='HOST/TIAMCP.HOST.EXE'; bytes=$entries[0].bytes; sha256=$entries[0].sha256})
Assert-Rejected { & $verifier -BundlePath $testRoot } 'Case-insensitive duplicate manifest entry accepted.'
Write-TestManifest @($entries | Select-Object -Skip 1)
Assert-Rejected { & $verifier -BundlePath $testRoot } 'Missing required manifest entry accepted.'
Write-TestManifest $entries
$missing = Join-Path $testRoot $required[0]
Move-Item -LiteralPath $missing -Destination ($missing + '.saved')
Assert-Rejected { & $verifier -BundlePath $testRoot } 'Missing file accepted.'
Move-Item -LiteralPath ($missing + '.saved') -Destination $missing
[IO.File]::WriteAllText($missing, 'test fixture only, never executE')
Assert-Rejected { & $verifier -BundlePath $testRoot } 'Same-length corrupt file accepted.'
[IO.File]::WriteAllText($missing, 'test fixture only, never execute')
foreach ($extension in @('dll','exe','ps1','json','config')) {
    $extra = Join-Path $testRoot ('extra.' + $extension)
    [IO.File]::WriteAllText($extra, 'extra')
    Assert-Rejected { & $verifier -BundlePath $testRoot } ('Unlisted runtime file accepted: ' + $extension)
    Remove-Item -LiteralPath $extra
}
$extraText = Join-Path $testRoot 'extra.txt'
[IO.File]::WriteAllText($extraText, 'notes')
Assert-Check ((& $verifier -BundlePath $testRoot).verified) 'Default verification should identify but permit non-runtime notes.'
Assert-Rejected { & $verifier -BundlePath $testRoot -Strict } 'Strict verification accepted an extra file.'
Remove-Item -LiteralPath $extraText
$vendor = Join-Path $testRoot 'Siemens.Engineering.dll'
[IO.File]::WriteAllText($vendor, 'vendor placeholder')
Write-TestManifest (@($entries) + @{path='Siemens.Engineering.dll';bytes=(Get-Item -LiteralPath $vendor).Length;sha256=(Get-FileHash -LiteralPath $vendor).Hash})
Assert-Rejected { & $verifier -BundlePath $testRoot } 'Manifest-listed Siemens binary accepted.'
Remove-Item -LiteralPath $vendor
# Exercise pre-launch validation itself. The placeholder EXE is never executed:
# assertions require our specific guard error, not a native process start error.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-TiaMcp.ps1') -Destination (Join-Path $testRoot 'scripts/Start-TiaMcp.ps1') -Force
Copy-Item -LiteralPath $verifier -Destination (Join-Path $testRoot 'scripts/Verify-TiaMcpBundle.ps1') -Force
$entries = @($required | ForEach-Object { $file = Get-Item -LiteralPath (Join-Path $testRoot $_); @{path=$_; bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} })
Write-TestManifest $entries
$testLauncher = Join-Path $testRoot 'scripts/Start-TiaMcp.ps1'
Assert-Rejected { & $testLauncher -ArtifactsDirectory (Join-Path $testRoot 'artifacts') } 'Launcher allowed bundle-local artifacts.' 'ArtifactsDirectory must be outside'
Assert-Rejected { & $testLauncher -ArtifactsDirectory 'relative/path' } 'Launcher accepted a relative artifact directory.' 'must be fully qualified'
$unlistedRuntime = Join-Path $testRoot 'injected.dll'
[IO.File]::WriteAllText($unlistedRuntime, 'never execute')
Assert-Rejected { & $testLauncher -ArtifactsDirectory (Join-Path $repoRoot 'artifacts') } 'Launcher bypassed bundle verification.' 'unlisted file'
Remove-Item -LiteralPath $unlistedRuntime
$childScript = Join-Path $testRoot 'scripts/Verify-RestrictedModulePath.ps1'
[IO.File]::WriteAllText($childScript, @'
param([string]$Launcher, [string]$ArtifactPath)
try { & $Launcher -ArtifactsDirectory $ArtifactPath; exit 8 }
catch {
    if ($_.Exception.Message -match 'ArtifactsDirectory must be outside') { exit 0 }
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 9
}
'@)
$childFile = Get-Item -LiteralPath $childScript
Write-TestManifest (@($entries) + @{path='scripts/Verify-RestrictedModulePath.ps1'; bytes=$childFile.Length; sha256=(Get-FileHash -LiteralPath $childScript -Algorithm SHA256).Hash.ToLowerInvariant()})
$previousModulePath = $env:PSModulePath
try {
    $env:PSModulePath = Join-Path $testRoot 'intentionally-no-modules'
    & powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $childScript -Launcher $testLauncher -ArtifactPath (Join-Path $testRoot 'artifacts')
    Assert-Check ($LASTEXITCODE -eq 0) 'Legacy PowerShell child launcher requires an inherited Utility module path.'
} finally { $env:PSModulePath = $previousModulePath }
# Offline manifests retain the same integrity boundary but exclude native workers.
Remove-Item -LiteralPath $childScript
foreach ($name in $required | Where-Object { $_ -like 'workers/*' }) { Remove-Item -LiteralPath (Join-Path $testRoot $name) }
$required = @($required | Where-Object { $_ -notlike 'workers/*' })
$notices = @('LICENSE','NOTICE','third-party/host-packages.json','third-party/ModelContextProtocol.LICENSE','third-party/Microsoft.Extensions.LICENSE.txt','third-party/ModelContextProtocol.NOTICES.txt','third-party/Microsoft.Extensions.NOTICES.txt')
foreach ($name in $notices) {
    $target = Join-Path $testRoot $name
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($target, 'fixture notice')
}
$entries = @($required + $notices | ForEach-Object { $file = Get-Item -LiteralPath (Join-Path $testRoot $_); @{path=$_; bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()} })
function Write-OfflineManifest($Rows, [string]$Kind = 'offline') { @{bundleKind=$Kind;files=@($Rows)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8 }
Write-OfflineManifest $entries
Assert-Check ((& $verifier -BundlePath $testRoot -Strict).bundleKind -ceq 'offline') 'Portable bundle requires native workers.'
foreach ($name in $notices) {
    Write-OfflineManifest @($entries | Where-Object { $_.path -ne $name })
    Assert-Rejected { & $verifier -BundlePath $testRoot } ('Offline notice omission accepted: ' + $name) 'required runtime file'
}
Write-OfflineManifest $entries 'OFFLINE'
Assert-Rejected { & $verifier -BundlePath $testRoot } 'Unknown bundle kind accepted.' 'Unsupported bundle kind'
Write-OfflineManifest $entries 'engineering'
Assert-Rejected { & $verifier -BundlePath $testRoot } 'Engineering bundle without workers accepted.' 'required runtime file'
Write-OfflineManifest $entries
foreach ($name in @('workers/openness/TiaMcp.Openness.V21.exe','host/TiaMcp.Simulator.V8.dll')) {
    $target = Join-Path $testRoot $name
    [IO.File]::WriteAllText($target, 'never execute')
    Assert-Rejected { & $verifier -BundlePath $testRoot } 'Offline bundle admitted a native worker.' 'cannot contain native workers'
    Remove-Item -LiteralPath $target
}
Assert-Rejected { & $testLauncher -Diagnose } 'Portable launcher admitted native diagnosis.' 'Offline bundles do not support'
Assert-Rejected { & $testLauncher -InteractiveSession } 'Portable launcher admitted interactive engineering.' 'Offline bundles do not support'
Assert-Rejected { & $testLauncher -TiaApiDirectory 'C:\TIA API' } 'Portable launcher admitted TIA override.' 'Offline bundles do not support'
Assert-Rejected { & $testLauncher -SimulatorApiDirectory 'C:\SIM API' } 'Portable launcher admitted simulator override.' 'Offline bundles do not support'
$link = Join-Path $testRoot 'linked-host'
New-Item -ItemType Junction -Path $link -Target (Join-Path $testRoot 'host') | Out-Null
try {
    Assert-Rejected { & $verifier -BundlePath $testRoot -Strict } 'Portable bundle admitted a junction.' 'reparse point'
    Assert-Rejected { & $verifier -BundlePath $link -Strict } 'Bundle root followed a junction.' 'reparse point'
} finally { Remove-Item -LiteralPath $link -Force }
Assert-Check (Test-Path -LiteralPath (Join-Path $testRoot 'host/TiaMcp.Host.exe')) 'Junction removal deleted its target.'
# Only this invocation's generated directory is eligible for recursive cleanup.
$resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
$expectedPrefix = (Join-Path $repoRoot 'artifacts') + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedTestRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolvedTestRoot -Leaf) -notmatch '^deployment-tests-[a-f0-9]{32}$') { throw 'Test cleanup target escaped its workspace.' }
Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
Write-Output "PASS $checks deployment checks: argument boundaries, encoded SSH, strict host identity, overrides and bundle integrity rejection."
