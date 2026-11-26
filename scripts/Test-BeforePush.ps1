<#
.SYNOPSIS
Runs the offline checks that the files changed on this branch can break, before a push instead of after it.

.DESCRIPTION
Compares the branch, the working tree and untracked files with the merge base of -Base and runs, in this order:
C# whitespace formatting of the gated projects, ruff, a Release build with the analyzer errors (when C# or build files
changed), the stubless worker compile (when worker sources changed), the source bindings (when sources changed), the
feature contract checks and STATUS/TODO counts (when contracts, the matrix or those pages changed) and the Python
test modules that changed or import a changed script, in the same environment as scripts/Test-Offline.ps1.
-Full runs scripts/Test-Offline.ps1 -Suite All afterwards. Nothing native runs.
#>
param(
    [string]$Base = 'origin/main',
    [switch]$Full
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    $mergeBase = (git merge-base $Base HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $mergeBase) { throw "No merge base with $Base; fetch it first." }
    $changed = @(
        git diff --name-only "$mergeBase" HEAD
        git diff --name-only HEAD
        git ls-files --others --exclude-standard
    ) | Where-Object { $_ } | ForEach-Object { $_ -replace '\\', '/' } | Sort-Object -Unique
    Write-Output "Changed files against $Base ($($mergeBase.Substring(0, 8))): $($changed.Count)"
    $failures = [Collections.Generic.List[string]]::new()
    function Invoke-Check([string]$Name, [scriptblock]$Body) {
        Write-Output "== $Name"
        $global:LASTEXITCODE = 0
        # Native tools report through the exit code, Test-Offline.ps1 and Test-WorkerCompile.ps1 by throwing.
        try { & $Body; $passed = $LASTEXITCODE -eq 0 } catch { Write-Output $_.Exception.Message; $passed = $false }
        if ($passed) { Write-Output "PASS $Name" } else { $failures.Add($Name); Write-Output "FAIL $Name" }
    }

    # Keep this list in step with the format step of the lint job in .github/workflows/offline.yml.
    $formatted = @('src/TiaMcp.Core/', 'src/TiaMcp.Host/')
    if ($changed | Where-Object { $path = $_; $_ -like '*.cs' -and ($formatted | Where-Object { $path.StartsWith($_) }) }) {
        Invoke-Check 'C# whitespace formatting' { dotnet format whitespace --folder --include @formatted --verify-no-changes --verbosity quiet }
    }

    if ($changed -like '*.py') {
        $ruff = Get-Command ruff -ErrorAction SilentlyContinue
        if ($ruff) { Invoke-Check 'ruff' { ruff check } }
        else {
            python -m ruff --version *> $null
            if ($LASTEXITCODE -eq 0) { Invoke-Check 'ruff' { python -m ruff check } }
            else { Write-Warning 'ruff is not installed (python -m pip install ruff==0.17.4); CI job lint-python still runs it.' }
        }
    }

    if ($changed | Where-Object { $_ -match '\.(cs|csproj|props|targets|slnx|globalconfig)$' }) {
        Invoke-Check 'Release build with analyzer errors' { dotnet build TiaMcp.slnx -c Release -nologo -v:q -clp:NoSummary }
    }
    if ($changed -match '^src/TiaMcp\.(Openness\.V21|Openness\.V19|Simulator\.V8|TestSuite\.V21|AddIn\.V21)/') {
        Invoke-Check 'Stubless worker compile' { & (Join-Path $PSScriptRoot 'Test-WorkerCompile.ps1') }
    }

    # The bindings pin the SHA-256 of every implementing source file; any source edit needs ./scripts/Refresh-Contracts.ps1.
    if ($changed -match '^(src/|contracts/|scripts/api_coverage)') {
        Invoke-Check 'Source bindings' { $env:PYTHONUTF8 = '1'; python -B scripts/api_coverage_bindings.py validate contracts/api-coverage/implementation-bindings.v1.json --allow-missing-evidence | Out-Null }
    }
    if ($changed -match '^(contracts/|docs/plans/release-(matrix|obligations))') {
        Invoke-Check 'Feature contracts' { $env:PYTHONUTF8 = '1'; python -B scripts/test_contracts.py }
    }
    if ($changed -match '^(contracts/feature-catalog|docs/plans/release-matrix|docs/STATUS\.md|docs/TODO\.md)') {
        Invoke-Check 'STATUS and TODO counts' { python -B scripts/status_counts.py }
    }

    # Python test modules: changed ones, and those importing a changed module from scripts/.
    $testFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'scripts'), (Join-Path $repoRoot 'scripts/tests') -Filter 'test_*.py' -File)
    $changedModules = @($changed | Where-Object { $_ -match '^scripts/(tests/)?[^/]+\.py$' } | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) })
    $selected = @(foreach ($file in $testFiles) {
        $module = [IO.Path]::GetFileNameWithoutExtension($file.Name)
        if ($module -in $changedModules) { $file.Name; continue }
        if ($changedModules.Count) {
            $pattern = '(?m)^\s*(import|from)\s+(' + (($changedModules | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b'
            if ([regex]::IsMatch([IO.File]::ReadAllText($file.FullName), $pattern)) { $file.Name }
        }
    }) | Sort-Object -Unique
    if ($selected.Count) {
        Write-Output "Python test modules: $($selected -join ', ')"
        Invoke-Check 'Python tests (selected)' { & (Join-Path $PSScriptRoot 'Test-Offline.ps1') -Suite Python -PythonTest $selected }
    }

    if ($Full) { Invoke-Check 'Full offline validation' { & (Join-Path $PSScriptRoot 'Test-Offline.ps1') -Suite All } }

    if ($failures.Count) { throw "Failed before push: $($failures -join ', ')" }
    Write-Output 'PASS Checks for the changed files. CI still runs every suite.'
} finally {
    Pop-Location
}
