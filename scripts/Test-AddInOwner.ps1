param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Select a fresh owner validation directory.' }
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$sdk = & $dotnet --version
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the current .NET SDK.' }
$compiler = Join-Path (Split-Path $dotnet -Parent) "sdk/$sdk/Roslyn/bincore/csc.dll"
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$globalPackages = (& $dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate restored packages.' }
[xml]$workerProject = Get-Content -LiteralPath (Join-Path $repoRoot 'src/TiaMcp.Openness.V21/TiaMcp.Openness.V21.csproj') -Raw
$newtonsoftVersion = $workerProject.SelectSingleNode('/Project/ItemGroup/PackageReference[@Include="Newtonsoft.Json"]').GetAttribute('Version')
$newtonsoft = @((Join-Path $repoRoot '.packages'), $globalPackages) |
    ForEach-Object { Join-Path $_ "newtonsoft.json/$newtonsoftVersion/lib/net45/Newtonsoft.Json.dll" } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$newtonsoft) { throw 'Restore the existing TiaMcp.Tests dependencies before this check.' }
$references = @('mscorlib.dll','System.dll','System.Core.dll') | ForEach-Object { Join-Path $framework $_ }
foreach ($path in @($compiler) + $references) { if (!(Test-Path -LiteralPath $path)) { throw "Required compiler/runtime unavailable: $path" } }
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -LiteralPath $newtonsoft -Destination (Join-Path $output 'Newtonsoft.Json.dll')
$sources = @('src/TiaMcp.Openness.V21/CapturedProcessIdentity.cs', 'src/TiaMcp.Openness.V21/OpennessSession.AddInOwner.cs',
    'src/TiaMcp.Openness.V21/BridgeException.cs', 'fixtures/addin-owner/FrameworkFixture.cs') | ForEach-Object { Join-Path $repoRoot $_ }
$arguments = @('/nologo','/noconfig','/nostdlib+','/target:exe','/platform:x64','/langversion:latest',('/out:' + (Join-Path $output 'owner-fixture.exe')))
$arguments += (@($references) + @($newtonsoft)) | ForEach-Object { '/reference:' + $_ }
$arguments += $sources
& $dotnet $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Framework owner fixture compilation failed.' }
& (Join-Path $output 'owner-fixture.exe') (Join-Path $output 'result.json')
if ($LASTEXITCODE -ne 0) { throw 'Framework owner fixture execution failed.' }
