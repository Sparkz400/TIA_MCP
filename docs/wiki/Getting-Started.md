There are two ways to run TIA MCP. Pick the one that matches your machine.

## Without Siemens software (portable)

Inspects supplied documents and saved project reviews; it cannot open TIA projects or start simulations.

Requirements: Windows, .NET 10 SDK (or the .NET 10 ASP.NET Core runtime for the packaged build). Git is optional.

```powershell
dotnet build src/TiaMcp.Host/TiaMcp.Host.csproj -c Release --artifacts-path artifacts/host-only
```

Point a stdio MCP client at `artifacts/host-only/bin/TiaMcp.Host/release/TiaMcp.Host.exe` with an absolute path. Add `--artifacts <dir>` for retained uploads and reports, and `--no-native-workers` to refuse Siemens calls.

For OPC UA trust that survives Host restarts, explicitly create a protected client identity and add `--ua-client-identity <name>` to the Host arguments. Setup, public certificate export and rotation are described in the [OPC UA identity workflow](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/opcua-runtime.md#protected-persistent-client-identity). The option needs a Windows user profile; it does not configure server trust or resume sessions.

For a relocatable ZIP, run `./scripts/Package.ps1 -Offline`. It writes the bundle, hashes, notices and an MCP configuration; the bundle name and its manifest carry the product version. See [portable deployment](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/deployment.md#portable-package-without-siemens). Version tags attach the same verified ZIP to a draft GitHub release that the maintainer publishes ([releasing](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/releasing.md)).

## On an engineering workstation

Requirements:

- Windows with **TIA Portal V21** and **Openness**, configured for your user ([Openness registration](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/openness-application-registration.md))
- **PLCSIM Advanced V8** with a usable license and a supported CPU/firmware, for simulation.
  The default Softbus mode needs no network setup and covers simulation, download, online compare and upload.
  - Only the runtime OPC UA and web tools against PLCSIM need TCP/IP: set PLCSIM *Online Access* to the
    PLCSIM Virtual Ethernet Adapter with a static address, and create the instance with
    `communicationInterface: "TCPIP"` at `sim_create` ([simulation](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/simulation.md#loading-an-engineering-program)).
    Runtime writes and RUN/STOP also need Windows to route the instance's address through that adapter, so give
    it an address in the instance's subnet ([owned PLCSIM writes](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/physical-runtime-sessions.md#owned-plcsim-writes-and-runstop))
- .NET 10 SDK and .NET Framework 4.8 reference assemblies
- PowerShell 7, Python 3.13 and Node.js 24 for the development checks

From the repository root:

```powershell
.\scripts\Probe-Environment.ps1
.\scripts\Build.ps1
.\scripts\Test.ps1 -SkipBuild
```

If the probe reports missing prerequisites, follow [environment diagnostics](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/environment-diagnostics.md).

Combined library install: on every Host, `change_apply` installs a prepared PLC `library_integrate` plan in one step for the natively qualified shapes only and refuses OB insertion and every other shape. The former opt-in flag `--enable-library-integrate-apply` is still accepted and changes nothing; `--disable-library-integrate-apply` turns combined apply off. See [combined apply](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/plc-library-integration-recipes.md#combined-apply).

Optional TIA Add-In: `./scripts/Publish-AddIn.ps1` builds the companion with the installed Siemens Publisher and writes an enrollment file. Install the package in TIA yourself, then start the Host with `--addin-handoff --addin-enrollment <file>`. Steps and limits are in [Add-In companion](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/addin-selections.md#native-companion-build-publish-and-enroll); one native selection handoff is verified (`local.addin.companion.20261029`).

The launcher scripts do not pass `--addin-handoff`, `--addin-enrollment`, `--enable-library-integrate-apply`, `--disable-library-integrate-apply`, `--library-limits-file` or `--no-native-workers`; start the Host directly for those. Every option, its default and its range is listed under [Host options](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/deployment.md#host-options).

Vendor libraries can have additional CPU, tag-table and product requirements.
Check the [PAL/DMC intake and prerequisite review](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/verification/vendor-intake-review-20261025.md)
before using the retained packages; receipt verification does not qualify native compatibility.

## Connect an MCP client

Configure a stdio server with your actual checkout path:

```json
{
  "mcpServers": {
    "tia-v21": {
      "command": "C:\\path\\to\\TIA_MCP\\src\\TiaMcp.Host\\bin\\Debug\\net10.0-windows\\TiaMcp.Host.exe",
      "args": ["--timeout-seconds", "300"]
    }
  }
}
```

Then ask the client: *Use `tia_discover` to list the available TIA Portal sessions.* For a command-line check, run `python .\scripts\smoke_mcp.py`.

The Host advertises about 290 tools. If your client limits tools per server, add `"--toolsets", "core,simulation"` (or other groups) to `args`. The server also offers workflow prompts such as `safe_change` and `block_test`. Groups, prompts, resources, completions, logging and progress notifications are described in [MCP surface](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/mcp-surface.md).

## Next steps

- Make your first change with the [engineering workflow](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/engineering-workflows.md).
- Browse a project in the [Project Explorer](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/project-explorer.md).
- Run PLCSIM tests with [simulation](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/simulation.md) and [block testing](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/block-testing.md).
- Ask the server what is supported: `feature_catalog` and `target_capabilities`.
