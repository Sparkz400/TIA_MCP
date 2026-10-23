#Requires -Version 5.1
<#
.SYNOPSIS
Restores the recorded PLCSIM allocation-policy experiment; retains trial actions for audit.
.DESCRIPTION
The allocation-policy experiments did NOT fix startup on this machine. Both were
restored and are not recommended as a fix. This script defaults to verified rollback.
The trials addressed an observed allocation overlapping Vplc1500.dll's fixed image
base. Only BottomUp and HighEntropy were enabled for the exact installed
instance executable. Forced image relocation and system-wide settings are untouched.
Requires an administrator token. Refuses to overwrite an existing per-image policy.
#>
[CmdletBinding()]
param([ValidateSet('Apply', 'KeepBottomUp', 'Restore')][string]$Action = 'Restore')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$recordPath = Join-Path $repoRoot 'artifacts/simulator-allocation-policy.json'
$instancePath = 'C:\Program Files (x86)\Common Files\Siemens\PLCSIMADV\Siemens.Simatic.Simulation.Runtime.Instance.x64.exe'
$registryPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\Siemens.Simatic.Simulation.Runtime.Instance.x64.exe'
$policyIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $policyPrincipal = [Security.Principal.WindowsPrincipal]::new($policyIdentity)
    if (-not $policyPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator approval is required for this per-application Windows setting.'
    }
} finally { $policyIdentity.Dispose() }

if ($Action -eq 'Apply') {
    if (-not (Test-Path -LiteralPath $instancePath -PathType Leaf)) { throw 'Installed V8 instance executable not found.' }
    if (Test-Path -LiteralPath $registryPath) { throw 'An existing per-image policy is present. Refusing to overwrite it.' }
    if (Test-Path -LiteralPath $recordPath) { throw 'An allocation-policy record already exists. Inspect it before applying another change.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $instancePath
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Siemens') { throw 'Expected a valid Siemens-signed executable.' }
    $record = [ordered]@{
        executable = $instancePath
        registryPath = $registryPath
        priorImagePolicyExisted = $false
        enabled = @('BottomUp', 'HighEntropy')
        forcedRelocationChanged = $false
        systemPolicyChanged = $false
        executableSha256 = (Get-FileHash -LiteralPath $instancePath -Algorithm SHA256).Hash
        requestedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        applied = $false
    }
    $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $recordPath -Encoding UTF8
    Set-ProcessMitigation -Name $instancePath -Enable BottomUp,HighEntropy
    $observed = Get-ProcessMitigation -Name $instancePath
    if ($observed.Aslr.BottomUp.ToString() -ne 'ON' -or $observed.Aslr.HighEntropy.ToString() -ne 'ON') {
        throw 'Requested allocation policy was not confirmed. Inspect the setting and restore using this record if needed.'
    }
    $record.applied = $true
    $record['observed'] = $observed
    $record['appliedAtUtc'] = [DateTimeOffset]::UtcNow.ToString('o')
    $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $observed.Aslr | Format-List
    Write-Output "Recorded policy change: $recordPath"
} else {
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
    if ($record.executable -cne $instancePath -or $record.registryPath -cne $registryPath -or
        $record.priorImagePolicyExisted -ne $false -or $record.forcedRelocationChanged -ne $false -or
        $record.systemPolicyChanged -ne $false -or ($record.enabled -join ',') -cne 'BottomUp,HighEntropy') {
        throw 'The record does not describe this script-owned change.'
    }
    if (-not (Test-Path -LiteralPath $instancePath -PathType Leaf)) { throw 'Installed V8 instance executable not found.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $instancePath
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Siemens' -or
        (Get-FileHash -LiteralPath $instancePath -Algorithm SHA256).Hash -ne $record.executableSha256) {
        throw 'The installed executable no longer matches the recorded Siemens-signed target.'
    }

    # The installed cmdlet passes a non-null buffer with -Remove/-Reset, which
    # ntdll rejects with C000000D. A valid native Remove would clear all options.
    # Use the same normal ASLR API as the cmdlet, changing only our two fields.
    if (-not ('TiaMcpAllocationPolicyNative' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TiaMcpAllocationPolicyNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct AslrPolicy
    {
        public UInt64 ForceRelocateImages;
        public UInt64 BottomUpRandomization;
        public UInt64 HighEntropyRandomization;
    }
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern Int32 RtlQueryImageMitigationPolicy(string imagePath, Int32 policy,
        UInt32 flags, ref AslrPolicy buffer, UInt32 bufferSize);
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern Int32 RtlSetImageMitigationPolicy(string imagePath, Int32 policy,
        UInt32 flags, ref AslrPolicy buffer, UInt32 bufferSize);
    private const string Target = @"C:\Program Files (x86)\Common Files\Siemens\PLCSIMADV\Siemens.Simatic.Simulation.Runtime.Instance.x64.exe";
    private static void Check(Int32 status, string operation)
    {
        if (status != 0) throw new InvalidOperationException(operation + " failed: 0x" + status.ToString("X8"));
    }
    public static AslrPolicy Read()
    {
        var policy = new AslrPolicy();
        Check(RtlQueryImageMitigationPolicy(Target, 1, 0, ref policy, 24), "Read exact-image ASLR policy");
        return policy;
    }
    public static AslrPolicy ClearAllocationOverrides(AslrPolicy expected, bool keepBottomUp)
    {
        var current = Read();
        if (current.ForceRelocateImages != expected.ForceRelocateImages ||
            current.BottomUpRandomization != expected.BottomUpRandomization ||
            current.HighEntropyRandomization != expected.HighEntropyRandomization)
            throw new InvalidOperationException("ASLR policy changed after the recorded preflight read.");
        current.HighEntropyRandomization = 0; // NOTSET: inherit the system policy.
        if (!keepBottomUp) current.BottomUpRandomization = 0;
        Check(RtlSetImageMitigationPolicy(Target, 1, 0, ref current, 24), "Write exact-image ASLR policy");
        return Read();
    }
}
'@
    }
    $before = Get-ProcessMitigation -Name $instancePath
    if ($null -eq $before -or $before.ProcessName -ne $instancePath) { throw 'Exact-path mitigation policy was not found.' }
    $nativeBefore = [TiaMcpAllocationPolicyNative]::Read()
    $expectedBottomUp = if ($record.applied) { [uint64]1 } else { [uint64]0 }
    $expectedHighEntropy = if ($record.applied -and -not $record.keptBottomUpAtUtc) { [uint64]1 } else { [uint64]0 }
    if ($nativeBefore.BottomUpRandomization -ne $expectedBottomUp -or
        $nativeBefore.HighEntropyRandomization -ne $expectedHighEntropy) {
        throw 'Allocation overrides changed outside this recorded workflow. Refusing to overwrite them.'
    }
    if ($Action -eq 'KeepBottomUp' -and $nativeBefore.BottomUpRandomization -ne 1) {
        throw 'KeepBottomUp requires the previously applied BottomUp override.'
    }
    $unrelatedBefore = $before | Select-Object -Property * -ExcludeProperty Aslr | ConvertTo-Json -Depth 12 -Compress
    $transition = [pscustomobject][ordered]@{
        action = $Action
        requestedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        nativeBefore = $nativeBefore
        observedBefore = $before
        completed = $false
    }
    $record | Add-Member -NotePropertyName transitions -NotePropertyValue (@($record.transitions) + @($transition) | Where-Object { $null -ne $_ }) -Force
    $record | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $recordPath -Encoding UTF8
    try {
        $nativeAfter = [TiaMcpAllocationPolicyNative]::ClearAllocationOverrides($nativeBefore, $Action -eq 'KeepBottomUp')
        $after = Get-ProcessMitigation -Name $instancePath
        $transition | Add-Member -NotePropertyName nativeAfter -NotePropertyValue $nativeAfter
        $transition | Add-Member -NotePropertyName observedAfter -NotePropertyValue $after
        $wantedBottomUp = if ($Action -eq 'KeepBottomUp') { [uint64]1 } else { [uint64]0 }
        $wantedBottomUpText = if ($Action -eq 'KeepBottomUp') { 'ON' } else { 'NOTSET' }
        if ($nativeAfter.ForceRelocateImages -ne $nativeBefore.ForceRelocateImages -or
            $nativeAfter.BottomUpRandomization -ne $wantedBottomUp -or $nativeAfter.HighEntropyRandomization -ne 0 -or
            $null -eq $after -or $after.Aslr.BottomUp.ToString() -ne $wantedBottomUpText -or
            $after.Aslr.HighEntropy.ToString() -ne 'NOTSET') {
            throw 'ASLR readback did not match the requested scoped change.'
        }
        $unrelatedAfter = $after | Select-Object -Property * -ExcludeProperty Aslr | ConvertTo-Json -Depth 12 -Compress
        if ($unrelatedAfter -cne $unrelatedBefore) { throw 'Unrelated mitigation policy changed during the operation; inspect the recorded readbacks.' }
        $transition.completed = $true
        $transition | Add-Member -NotePropertyName verifiedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o'))
        if ($Action -eq 'Restore') {
            $record | Add-Member -NotePropertyName restoredAtUtc -NotePropertyValue $transition.verifiedAtUtc -Force
            $record | Add-Member -NotePropertyName applied -NotePropertyValue $false -Force
        } else {
            $record | Add-Member -NotePropertyName keptBottomUpAtUtc -NotePropertyValue $transition.verifiedAtUtc -Force
        }
    } catch {
        $transition | Add-Member -NotePropertyName error -NotePropertyValue $_.Exception.Message -Force
        throw
    } finally {
        $record | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $recordPath -Encoding UTF8
    }
    $after.Aslr | Format-List
    Write-Output "Verified $Action; unrelated mitigations preserved. Record: $recordPath"
}
