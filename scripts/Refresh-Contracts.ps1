<#
.SYNOPSIS
Refreshes the pinned source hashes in contracts/api-coverage/implementation-bindings.v1.json after intended source
edits, validates the result the way CI does, and stages it.

.DESCRIPTION
The bindings pin the SHA-256 of every source file that implements a feature, so any edit to such a file fails the
"Python contracts and harnesses" check with "Stale source bindings". Run this from the repository root after the edit
and commit the refreshed file with the change. The refresh rewrites the whole file, so after rebasing onto a newer
main, run it again instead of merging the file by hand.
#>
param([switch]$NoStage)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$binding = 'contracts/api-coverage/implementation-bindings.v1.json'
Push-Location $repoRoot
try {
    python -B scripts/api_coverage_bindings.py refresh $binding
    if ($LASTEXITCODE -ne 0) { throw 'Refreshing the source bindings failed.' }
    # Same options as Test-Offline.ps1: historical native evidence is not in a clean checkout.
    python -B scripts/api_coverage_bindings.py validate $binding --allow-missing-evidence | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The refreshed source bindings do not validate; the error above names the cause.' }
    if (-not $NoStage) { git add -- $binding; if ($LASTEXITCODE -ne 0) { throw "git add $binding failed." } }
    git diff --cached --stat -- $binding
} finally { Pop-Location }
