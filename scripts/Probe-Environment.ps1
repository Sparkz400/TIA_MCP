#Requires -Version 5.1
<#
.SYNOPSIS
Emits a read-only, JSON environment report for the TIA V21 prototype.
.DESCRIPTION
Reads known installation paths, file/assembly metadata, the .NET runtime registry
value, and the current process's Windows token and ASLR mitigation policy. Does not initialize Siemens APIs,
start applications, inspect license keys, query PLCs, or modify machine settings.
Run this script in the same Windows session/token that will run the MCP worker.
Missing means a path was conclusively absent; denied/failed reads are unavailable.
#>
[CmdletBinding()]
param(
    [string]$TiaApiDirectory,
    [string]$FrameworkReferenceDirectory,
    [string]$StandardSimulatorDirectory,
    [string]$AdvancedSimulatorDirectory,
    [string]$AdvancedRuntimeDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$programFiles64 = $env:ProgramW6432
if ([string]::IsNullOrWhiteSpace($programFiles64)) { $programFiles64 = $env:ProgramFiles }
$programFiles32 = ${env:ProgramFiles(x86)}
if ([string]::IsNullOrWhiteSpace($programFiles32)) { $programFiles32 = $env:ProgramFiles }
function Get-RegisteredTiaApiDirectory {
    $hklm = $null
    $key = $null
    try {
        $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
        $key = $hklm.OpenSubKey('SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48')
        if ($null -ne $key) {
            $basePath = $key.GetValue('Siemens.Engineering.Base')
            if ($basePath -is [string] -and -not [string]::IsNullOrWhiteSpace($basePath)) {
                return [IO.Path]::GetDirectoryName($basePath)
            }
        }
    }
    finally {
        if ($null -ne $key) { $key.Dispose() }
        if ($null -ne $hklm) { $hklm.Dispose() }
    }
}

function Get-TiaApiDirectory {
    param([string]$ExplicitDirectory, [string]$DefaultDirectory)
    if ($ExplicitDirectory) { return $ExplicitDirectory }
    # Match the native worker; a denied registry read must not imply a missing installation.
    $registeredDirectory = Get-RegisteredTiaApiDirectory
    if ($registeredDirectory) { return $registeredDirectory }
    return $DefaultDirectory
}

$TiaApiDirectory = Get-TiaApiDirectory $TiaApiDirectory (Join-Path $programFiles64 'Siemens\Automation\Portal V21\PublicAPI\V21\net48')
if (-not $FrameworkReferenceDirectory) { $FrameworkReferenceDirectory = Join-Path $programFiles32 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8' }
if (-not $StandardSimulatorDirectory) { $StandardSimulatorDirectory = Join-Path $programFiles64 'Siemens\Automation\PLCSIM_V21' }
if (-not $AdvancedSimulatorDirectory) { $AdvancedSimulatorDirectory = Join-Path $programFiles32 'Siemens\Automation\PLCSIMADV' }
if (-not $AdvancedRuntimeDirectory) { $AdvancedRuntimeDirectory = Join-Path $programFiles32 'Common Files\Siemens\PLCSIMADV' }

function Get-ReadFailure {
    param([System.Management.Automation.ErrorRecord]$Record)
    $status = 'unavailable'
    if ($Record.CategoryInfo.Category -eq 'ObjectNotFound' -or
        $Record.Exception -is [System.IO.FileNotFoundException] -or
        $Record.Exception -is [System.IO.DirectoryNotFoundException]) { $status = 'missing' }
    return [pscustomobject]@{ status = $status; reason = $Record.Exception.Message }
}

function Get-PathEvidence {
    param([string]$Path, [switch]$AssemblyMetadata)
    try {
        $item = Get-Item -LiteralPath $Path -ErrorAction Stop
        $result = [ordered]@{ path = $item.FullName; status = 'present'; kind = 'file' }
        if ($item.PSIsContainer) { $result.kind = 'directory' }
        elseif ($item.Extension -in @('.dll', '.exe')) {
            $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($item.FullName)
            $result.fileVersion = $info.FileVersion
            $result.productVersion = $info.ProductVersion
            if ($AssemblyMetadata) {
                try {
                    # GetAssemblyName reads the manifest; it never executes vendor code.
                    $assembly = [System.Reflection.AssemblyName]::GetAssemblyName($item.FullName)
                    $result.assembly = [ordered]@{ status = 'present'; name = $assembly.Name; version = $assembly.Version.ToString() }
                }
                catch { $result.assembly = [ordered]@{ status = 'unavailable'; reason = $_.Exception.Message } }
            }
        }
        return [pscustomobject]$result
    }
    catch {
        $failure = Get-ReadFailure $_
        return [pscustomobject]@{ path = $Path; status = $failure.status; reason = $failure.reason }
    }
}

function Get-DirectoryFiles {
    param([string]$Path, [string]$Filter)
    try {
        $files = @(Get-ChildItem -LiteralPath $Path -File -Filter $Filter -ErrorAction Stop)
        return [pscustomobject]@{ status = 'read'; files = $files; reason = $null }
    }
    catch {
        $failure = Get-ReadFailure $_
        return [pscustomobject]@{ status = $failure.status; files = @(); reason = $failure.reason }
    }
}

function Get-TokenGroupEvidence {
    param([string]$GroupName)
    $identity = $null
    try {
        $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        $account = [System.Security.Principal.NTAccount]::new($env:COMPUTERNAME, $GroupName)
        $sid = $account.Translate([System.Security.Principal.SecurityIdentifier])
        $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
        return [pscustomobject]@{
            name = $GroupName
            status = 'read'
            presentInCurrentToken = $identity.Groups.Contains($sid)
            authorizedInCurrentToken = $principal.IsInRole($sid)
            scope = 'Current process token; not a local/domain directory membership audit.'
        }
    }
    catch {
        # A failed name/SID lookup can reflect access restrictions. It is not absence.
        return [pscustomobject]@{ name = $GroupName; status = 'unavailable'; reason = $_.Exception.Message }
    }
    finally { if ($null -ne $identity) { $identity.Dispose() } }
}

function Get-StartupEnvironmentEvidence {
    $scope = 'Current PowerShell probe process only. MCP host, worker and Siemens processes may have different mitigation policies; this is not proof of their settings or runtime compatibility.'
    try {
        if (-not ('TiaMcp.EnvironmentProbe.ProcessMitigations' -as [type])) {
            # Read-only Win32 query; no SetProcessMitigationPolicy or vendor DLL loading.
            # PROCESS_MITIGATION_ASLR_POLICY is one DWORD; ProcessASLRPolicy is enum value 1.
            # https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-process_mitigation_aslr_policy
            Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace TiaMcp.EnvironmentProbe {
    public static class ProcessMitigations {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMitigationPolicy(IntPtr process, int policy, out uint flags, UIntPtr length);

        public static uint ReadCurrentAslrFlags() {
            uint flags;
            // -1 is the current-process pseudo handle; it must not be closed.
            if (!GetProcessMitigationPolicy(new IntPtr(-1), 1, out flags, new UIntPtr(4)))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return flags;
        }
    }
}
'@
        }
        $flags = [TiaMcp.EnvironmentProbe.ProcessMitigations]::ReadCurrentAslrFlags()
        $namedFlags = [ordered]@{
            EnableBottomUpRandomization = ($flags -band 1) -ne 0
            EnableForceRelocateImages = ($flags -band 2) -ne 0
            EnableHighEntropy = ($flags -band 4) -ne 0
            DisallowStrippedImages = ($flags -band 8) -ne 0
        }
        $warnings = @()
        if ($namedFlags.EnableForceRelocateImages -and $namedFlags.DisallowStrippedImages) {
            $warnings += [pscustomobject]@{
                code = 'ForcedAslrDisallowsStrippedImages'
                message = 'This process forces image relocation and disallows images without relocation data. A fixed-base native simulator DLL can fail to load under this combination. Compare evidence from the actual launch environment; no mitigation settings were changed.'
            }
        }
        return [pscustomobject]@{
            status = 'read'
            processId = $PID
            policy = 'ProcessASLRPolicy'
            flags = $flags
            flagsHex = ('0x{0:X8}' -f $flags)
            namedFlags = $namedFlags
            enabledFlags = @($namedFlags.Keys | Where-Object { $namedFlags[$_] })
            warnings = $warnings
            scope = $scope
            settingsModified = $false
            simulatorDllLoadAttempted = $false
        }
    }
    catch {
        return [pscustomobject]@{
            status = 'unavailable'
            reason = $_.Exception.Message
            scope = $scope
            settingsModified = $false
            simulatorDllLoadAttempted = $false
        }
    }
}

$tiaFiles = Get-DirectoryFiles $TiaApiDirectory 'Siemens.Engineering*.dll'
$tiaAssemblies = @($tiaFiles.files | Sort-Object Name | ForEach-Object {
    [pscustomobject]@{
        binary = Get-PathEvidence $_.FullName -AssemblyMetadata
        xmlDocumentation = Get-PathEvidence ([System.IO.Path]::ChangeExtension($_.FullName, '.xml'))
    }
})

$apiRoot = Join-Path $AdvancedRuntimeDirectory 'API'
$apiVersions = @()
$apiEnumeration = [ordered]@{ status = 'read'; reason = $null }
try {
    $versionDirectories = @(Get-ChildItem -LiteralPath $apiRoot -Directory -ErrorAction Stop |
        Where-Object { $_.Name -match '^\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending)
    $apiVersions = @($versionDirectories | ForEach-Object {
        [pscustomobject]@{
            versionDirectory = $_.Name
            x64 = Get-PathEvidence (Join-Path $_.FullName 'Siemens.Simatic.Simulation.Runtime.Api.x64.dll') -AssemblyMetadata
            x86 = Get-PathEvidence (Join-Path $_.FullName 'Siemens.Simatic.Simulation.Runtime.Api.x86.dll') -AssemblyMetadata
            cppHeader = Get-PathEvidence (Join-Path $_.FullName 'SimulationRuntimeApi.h')
        }
    })
}
catch {
    $failure = Get-ReadFailure $_
    $apiEnumeration.status = $failure.status
    $apiEnumeration.reason = $failure.reason
}

$runtimeFramework = try {
    $runtime = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction Stop
    [pscustomobject]@{ status = 'read'; release = $runtime.Release; supportsNet48 = $runtime.Release -ge 528040 }
}
catch {
    $failure = Get-ReadFailure $_
    [pscustomobject]@{ status = $failure.status; reason = $failure.reason }
}

$report = [ordered]@{
    schemaVersion = 1
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    probe = [ordered]@{
        mode = 'metadata-only'
        powershellVersion = $PSVersionTable.PSVersion.ToString()
        is64BitProcess = [Environment]::Is64BitProcess
        searchScope = 'Explicit directory parameters, the registered V21 Openness API location (64-bit registry), then known setup defaults; no recursive search.'
        runtimeInitializationAttempted = $false
        licenseStatus = 'unverified'
    }
    tiaV21 = [ordered]@{
        apiDirectory = Get-PathEvidence $TiaApiDirectory
        enumerationStatus = $tiaFiles.status
        enumerationReason = $tiaFiles.reason
        assemblies = $tiaAssemblies
        schemas = Get-PathEvidence (Join-Path (Split-Path $TiaApiDirectory -Parent) 'Schemas')
        hardwareParameterDescriptions = Get-PathEvidence (Join-Path (Split-Path $TiaApiDirectory -Parent) 'HW Parameter description')
        attachmentStatus = 'unverified'
    }
    framework = [ordered]@{
        referenceDirectory = Get-PathEvidence $FrameworkReferenceDirectory
        referenceMscorlib = Get-PathEvidence (Join-Path $FrameworkReferenceDirectory 'mscorlib.dll') -AssemblyMetadata
        runtime = $runtimeFramework
    }
    currentToken = [ordered]@{
        groups = @((Get-TokenGroupEvidence 'Siemens TIA Openness'), (Get-TokenGroupEvidence 'PlcSimUsers'))
        caveat = 'Token membership and effective authorization differ in restricted sessions. A false authorization check must not be treated as proof that the user needs adding to the group.'
    }
    startupEnvironment = Get-StartupEnvironmentEvidence
    simulators = [ordered]@{
        standardV21 = [ordered]@{
            directory = Get-PathEvidence $StandardSimulatorDirectory
            executable = Get-PathEvidence (Join-Path $StandardSimulatorDirectory 'S7PLCSIMV21.exe')
            bundledRuntimeApi = Get-PathEvidence (Join-Path $StandardSimulatorDirectory 'resources\bin\wwwroot\assets\lib\runtime\Siemens.Simatic.Simulation.Runtime.Api.x64.dll') -AssemblyMetadata
            publicCoSimulationApi = 'Not established by bundled DLL presence. The installed V8 product comparison documents co-simulation for Advanced, not standard PLCSIM Vxx.'
        }
        advanced = [ordered]@{
            directory = Get-PathEvidence $AdvancedSimulatorDirectory
            executable = Get-PathEvidence (Join-Path $AdvancedSimulatorDirectory 'bin\Siemens.Simatic.PlcSim.Advanced.UserInterface.exe')
            runtimeManager = Get-PathEvidence (Join-Path $AdvancedRuntimeDirectory 'Siemens.Simatic.Simulation.Runtime.Manager.exe')
            apiDirectory = Get-PathEvidence $apiRoot
            apiEnumeration = $apiEnumeration
            apiVersions = $apiVersions
            functionManual = Get-PathEvidence (Join-Path $AdvancedSimulatorDirectory 'documents\en-US\s7-plcsim_advanced_function_manual_en-US.pdf')
            apiManual = Get-PathEvidence (Join-Path $AdvancedSimulatorDirectory 'documents\en-US\s7-plcsim_advanced_function_manual_API_en-US.pdf')
            callableRuntimeStatus = 'unverified'
            reason = 'Metadata verifies installed API binaries only. API initialization can start Runtime Manager; it is intentionally outside this read-only probe.'
        }
    }
}

$report | ConvertTo-Json -Depth 12
