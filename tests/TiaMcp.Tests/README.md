# Bridge tests

This package-free .NET 10 console runner returns exit code 1 if any check fails.
It tests the real `TiaMcp.Core` bridge and launches its own executable in fake-worker
mode through `dotnet`. It never loads Siemens assemblies, attaches to TIA, opens
engineering projects, or contacts PLCs.

From the repository root in PowerShell:

`scripts/Test-Offline.ps1` runs contract checks, all discovered Python unit tests,
the explorer model/client checks and twelve explicitly selected .NET 10 suites.
It uses a fresh output directory, retains a transcript and builds outside shared
`bin/obj` outputs. It needs Python, Node and the .NET 10 SDK, but no Siemens
installation. The full engineering-workstation gate remains `scripts/Test.ps1`.

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.dotnet'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet restore tests/TiaMcp.Tests/TiaMcp.Tests.csproj --ignore-failed-sources
dotnet run --project tests/TiaMcp.Tests/TiaMcp.Tests.csproj --no-restore
```

The process tests need Windows to permit a same-user named-pipe connection. A
restricted execution sandbox may block that connection with `UnauthorizedAccessException`
even when the program is correct. In that case run the already-built test DLL in
a normal user PowerShell session; do not weaken the production pipe ACL:

```powershell
dotnet tests/TiaMcp.Tests/bin/Debug/net10.0/TiaMcp.Tests.dll
```

Coverage includes UTF-8 split across reads, buffered LF/CRLF frames, incomplete
frames, strict byte limits, invalid encodings and envelopes, error detail lifetime,
real subprocess transport, concurrent caller serialization, operation-error
recovery, cancellation before/while waiting/after dispatch, terminal timeout and
protocol failures, no replay or automatic restart, startup failure, and disposal.
Pure linked engineering helpers also check exact native access classifications,
I/O address widths, source declaration scope and lexical boundaries, native XML
object/ID boundaries, prohibited DTDs, and the 143-character TIA recovery path
limit before any filesystem or project mutation.

Each subprocess records requests in a unique temporary directory. Tests check
those records to verify requests were sent once and never replayed; the directory
is removed after its worker observes EOF. No extra test framework or package
download is required.

After building the V21 worker, also run its compiled-IL lifetime guard:

```powershell
dotnet tests/TiaMcp.Tests/bin/Debug/net10.0/TiaMcp.Tests.dll --verify-worker src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe
```

This mode reads PE metadata and IL without loading the worker or Siemens
assemblies. It fails if process descriptors escape `PortalProcesses`, if that
boundary or any nested helper invokes disposal (including a compiler-generated
`IDisposable.Dispose` call), or if descriptors are accessed elsewhere. This
protects against accidental `TiaPortalProcess.Dispose`, which terminates TIA
instead of merely releasing a descriptor. The normal runner includes synthetic
safe and unsafe IL fixtures that verify the guard itself.
