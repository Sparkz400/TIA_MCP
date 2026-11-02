# Shared by setup and registration. Loading this file performs no machine changes.
function Assert-OpennessLocalPath {
    param([string]$Path)
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path -match '[\x00-\x1f"<>|?*]' -or $Path.Substring(2).Contains(':') -or
        @($Path.Substring(3) -split '[\\/]' | Where-Object { $_ -eq '.' -or $_ -eq '..' -or $_ -match '[. ]$' }).Count -gt 0) {
        throw 'Paths must be absolute local Windows drive paths, with no traversal, device/UNC syntax, stream or trailing-dot/space component.'
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    $ancestor = $full
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Paths must not traverse reparse points.'
        }
        $parent = [IO.Directory]::GetParent($ancestor)
        $ancestor = if ($parent) { $parent.FullName } else { $null }
    }
    return $full
}

function Test-OpennessEntryMatches {
    param($Entry, $Proposal)
    if ($Entry.status -ne 'read') { return $false }
    foreach ($name in @('Path','DateModified','FileHash')) {
        $values = @($Entry.values | Where-Object name -IEQ $name)
        if ($values.Count -ne 1 -or $values[0].kind -cne 'String') { return $false }
        $comparison = if ($name -eq 'Path') { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        if (-not [string]::Equals($values[0].value, $Proposal[$name], $comparison)) { return $false }
    }
    return $true
}

function Read-OpennessEntry {
    param($BaseKey, [string]$EntryPath)
    $key = $BaseKey.OpenSubKey($EntryPath, $false)
    if ($null -eq $key) { return [pscustomobject]@{status='missing'; existed=$false; values=@()} }
    try {
        $values = @($key.GetValueNames() | Sort-Object | ForEach-Object {
            [pscustomobject]@{name=$_;kind=$key.GetValueKind($_).ToString();value=$key.GetValue($_,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}
        })
        return [pscustomobject]@{status='read';existed=$true;values=$values}
    }
    finally { $key.Dispose() }
}

function Select-OpennessEntry {
    param($BaseKey, [string]$ApplicationPath, $Proposal)
    $app = $BaseKey.OpenSubKey($ApplicationPath, $false)
    if ($null -eq $app) { return ($ApplicationPath + '\Entry') }
    try { $names = @($app.GetSubKeyNames() | Sort-Object) }
    finally { $app.Dispose() }
    $samePath = $null
    foreach ($name in $names) {
        $path = $ApplicationPath + '\' + $name
        $entry = Read-OpennessEntry $BaseKey $path
        # Prefer an already-valid identity when TIA has accumulated duplicates.
        if (Test-OpennessEntryMatches $entry $Proposal) { return $path }
        $values = @($entry.values | Where-Object { $_.name -ieq 'Path' -and $_.kind -ceq 'String' })
        if (-not $samePath -and $values.Count -eq 1 -and [string]::Equals($values[0].value, $Proposal.Path, [StringComparison]::OrdinalIgnoreCase)) {
            $samePath = $path
        }
    }
    if ($samePath) { return $samePath }
    $name = 'Entry'
    for ($i = 1; $names -contains $name; $i++) { $name = "Entry ($i)" }
    return ($ApplicationPath + '\' + $name)
}

function Write-OpennessEntry {
    param($BaseKey, [string]$EntryPath, $Proposal, [scriptblock]$AfterWrite)
    $key = $BaseKey.CreateSubKey($EntryPath, $true)
    try {
        foreach ($name in @('Path','DateModified','FileHash')) { $key.SetValue($name, $Proposal[$name], [Microsoft.Win32.RegistryValueKind]::String) }
        $key.Flush()
    }
    finally { $key.Dispose() }
    if ($AfterWrite) { & $AfterWrite }
    $result = Read-OpennessEntry $BaseKey $EntryPath
    if (-not (Test-OpennessEntryMatches $result $Proposal)) { throw 'Registry readback did not match the proposed worker identity.' }
    return $result
}
