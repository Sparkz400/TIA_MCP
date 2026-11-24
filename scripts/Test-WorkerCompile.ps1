<#
.SYNOPSIS
Compiles the Siemens-dependent workers without any Siemens assembly and fails on every compiler error that a missing
Siemens assembly cannot explain.

.DESCRIPTION
The workers (V21, V19, Simulator, TestSuite, Add-In) reference Siemens assemblies that are only installed on
engineering workstations and are never redistributed, so the offline CI cannot build them. Without this check a change
can break a worker while every CI job stays green; in November two separately green pull requests merged a nested type
and a method with the same name into OpennessSession (CS0102) and main stopped building.

Each worker is compiled with empty API directories, its API presence checks and the developer Openness registration
switched off, and project references not built. The compiler then reports every use of a Siemens type as CS0246
(type or namespace not found) or CS0234 (namespace member not found), and only those. Any other error code is a real
defect in the project's own code, such as a duplicate member, a syntax error or a wrong call into its own types.

A CS0246 still fails when it names a namespace-level type declared in a file the worker does not compile, in a folder
it already links files from: the worker forgot to link that file (V19 lost BatchedAttributeReads that way). So does a
CS0234 inside a TiaMcp namespace that is not one of the dropped worker references. Compiler messages are localized,
so only the quoted names in them are read.

Limits: an error inside an expression whose type is a Siemens type is not reported, and neither is a missing project
type declared outside the folders the worker links from. The native build (scripts/Build.ps1 on a workstation) stays
the authority; this check only catches what can be caught without the vendor assemblies.
#>
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string[]]$Project = @('TiaMcp.Openness.V21','TiaMcp.Openness.V19','TiaMcp.Simulator.V8','TiaMcp.TestSuite.V21','TiaMcp.AddIn.V21'),
    [string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $WorkDirectory) { $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) ('tiamcp-worker-compile-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$emptyApi = Join-Path $WorkDirectory 'empty-api'
New-Item -ItemType Directory -Force -Path $emptyApi | Out-Null
# Overrides the targets that need the vendor assemblies or write the registry. Passed as a global property, so it also
# applies to any project MSBuild evaluates on the worker's behalf.
$override = Join-Path $WorkDirectory 'no-siemens.targets'
@'
<Project>
  <Target Name="CheckTiaApi" />
  <Target Name="CheckSimulatorApi" />
  <Target Name="RegisterDevOpennessWorker" />
  <!-- A referenced worker (TestSuite.V21 from V21) cannot build here and is checked on its own; its types then stay
       unresolved, which the compiler reports as CS0246 like a Siemens type. Removed before configurations are assigned,
       so no reference path reaches the compiler. -->
  <Target Name="DropWorkerProjectReferences" BeforeTargets="AssignProjectConfiguration">
    <ItemGroup><ProjectReference Remove="@(ProjectReference)" /></ItemGroup>
  </Target>
</Project>
'@ | Set-Content -LiteralPath $override -Encoding UTF8

$expected = @('CS0246', 'CS0234')
# A CS0246 that names a type declared in a file the worker does not compile, in a folder it already links from, is a
# file the worker forgot to link, not a missing Siemens type; V19 lost BatchedAttributeReads that way. Only types at
# namespace level count (indented 0 under a file-scoped namespace, 4 under a block namespace): nested types such as
# Core's Address record are never referenced by a bare name elsewhere, and Siemens has an Address, Device and Node too.
$typeDeclaration = '(?m)^(?<indent>[ ]*)(?<mods>(?:[a-z]+[ ]+)*)(?:class|struct|interface|enum|record(?:[ ]+(?:class|struct))?)[ ]+(?<name>[A-Za-z_]\w*)'
function Get-UnlinkedTypes([string]$csproj) {
    $projectDir = Split-Path $csproj -Parent
    $sourceFiles = { param($dir) Get-ChildItem -LiteralPath $dir -Recurse -Filter *.cs -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object FullName }
    $linked = @(([xml](Get-Content -LiteralPath $csproj -Raw)).SelectNodes('//Compile[@Include]') | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $projectDir $_.Include)) })
    # SDK projects compile every .cs file in their own folder; the workers do not change that default.
    $compiled = [Collections.Generic.HashSet[string]]::new([string[]](@(& $sourceFiles $projectDir) + $linked), [StringComparer]::OrdinalIgnoreCase)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $linked | ForEach-Object { Split-Path $_ -Parent } | Sort-Object -Unique | Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { & $sourceFiles $_ } | Where-Object { -not $compiled.Contains($_) } | ForEach-Object {
            $text = [IO.File]::ReadAllText($_)
            $topIndent = if ($text -match '(?m)^namespace[ ]+[\w.]+[ ]*;') { 0 } else { 4 }
            foreach ($m in [regex]::Matches($text, $typeDeclaration)) {
                if ($m.Groups['indent'].Length -eq $topIndent -and $m.Groups['mods'].Value -notmatch '\b(private|protected)\b') { [void]$names.Add($m.Groups['name'].Value) }
            }
        }
    return , $names
}
$failed = @()
foreach ($name in $Project) {
    $csproj = Join-Path $repoRoot "src/$name/$name.csproj"
    if (-not (Test-Path -LiteralPath $csproj)) { throw "Unknown worker project: $name" }
    $unlinkedTypes = Get-UnlinkedTypes $csproj
    # Namespaces of the worker projects whose references are dropped above stay unresolved on purpose (CS0234).
    $dropped = @(([xml](Get-Content -LiteralPath $csproj -Raw)).SelectNodes('//ProjectReference') | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Include) })
    $log = Join-Path $WorkDirectory "$name.log"
    & dotnet build $csproj -c $Configuration -nologo -v q -clp:NoSummary `
        "-p:TiaApiDirectory=$emptyApi" "-p:SimulatorApiDirectory=$emptyApi" "-p:CustomAfterMicrosoftCommonTargets=$override" `
        "--artifacts-path" (Join-Path $WorkDirectory "build/$name") *> $log
    $lines = Get-Content -LiteralPath $log
    $errors = @($lines | Where-Object { $_ -match ': error (?<code>[A-Z]+\d+):' } | Sort-Object -Unique)
    $unexpected = @($errors | Where-Object {
        if ($_ -notmatch ': error (?<code>[A-Z]+\d+):') { return $false }
        if ($expected -notcontains $Matches.code) { return $true }
        # CS0246 naming one of the project's own types, or CS0234 inside the project's own namespace. The message text is
        # localized (English 'X', German "X"), so only the quoted names are read: CS0246 quotes the type first.
        $code = $Matches.code
        $quoted = @([regex]::Matches(($_ -replace '^.*?: error CS\d+:', ''), "[`"'](?<q>[^`"']+)[`"']") | ForEach-Object { $_.Groups['q'].Value })
        if ($code -eq 'CS0246') { return $quoted.Count -gt 0 -and $unlinkedTypes.Contains(($quoted[0] -replace '<.*$', '')) }
        # CS0234 quotes the member, then the namespace.
        if ($quoted.Count -lt 2 -or $quoted[1] -notlike 'TiaMcp*') { return $false }
        $full = "$($quoted[1]).$($quoted[0])"
        return @($dropped | Where-Object { $_ -eq $full -or $_.StartsWith("$full.") }).Count -eq 0
    })
    $codes = @($errors | ForEach-Object { if ($_ -match ': error (?<code>[A-Z]+\d+):') { $Matches.code } } | Group-Object | Sort-Object Name | ForEach-Object { "$($_.Name) x$($_.Count)" })
    if ($errors.Count -eq 0 -and $LASTEXITCODE -ne 0) {
        # The build failed before the compiler ran (restore, MSBuild evaluation); that is a defect too.
        $unexpected = @($lines | Where-Object { $_ -match 'error' } | Select-Object -First 20)
        if (-not $unexpected) { $unexpected = @("dotnet build exited with $LASTEXITCODE and reported no error line; see $log") }
    }
    if ($unexpected.Count) {
        $failed += $name
        Write-Host "FAIL $name ($($codes -join ', '))"
        $unexpected | Select-Object -First 40 | ForEach-Object { Write-Host "  $($_.Trim())" }
    } else {
        Write-Host "PASS $name (only missing-Siemens errors: $(if ($codes) { $codes -join ', ' } else { 'none' }))"
    }
}
if ($failed.Count) {
    throw ("Worker compile check failed for: " + ($failed -join ', ') + ". These errors are not explained by the missing Siemens assemblies; reproduce with scripts/Test-WorkerCompile.ps1 -Project <name>, or build natively with scripts/Build.ps1 on a workstation.")
}
# The expected compiler failures leave dotnet's nonzero exit code behind; the verdict above is the result.
exit 0
