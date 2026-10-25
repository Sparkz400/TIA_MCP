There are two ways to run TIA MCP. Pick the one that matches your machine.

## Without Siemens software (portable)

Inspects supplied documents and saved project reviews; it cannot open TIA projects or start simulations.

Requirements: Windows, .NET 10 SDK (or the .NET 10 ASP.NET Core runtime for the packaged build). Git is optional.

```powershell
dotnet build src/TiaMcp.Host/TiaMcp.Host.csproj -c Release --artifacts-path artifacts/host-only
```

Point a stdio MCP client at `artifacts/host-only/bin/TiaMcp.Host/release/TiaMcp.Host.exe` with an absolute path. Add `--artifacts <dir>` for retained uploads and reports, and `--no-native-workers` to refuse Siemens calls.

For a relocatable ZIP, run `./scripts/Package.ps1 -Offline`. It writes the bundle, hashes, notices and an MCP configuration. See [portable deployment](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/deployment.md#portable-package-without-siemens).

## On an engineering workstation

Requirements:

- Windows with **TIA Portal V21** and **Openness**, configured for your user ([Openness registration](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/openness-application-registration.md))
- **PLCSIM Advanced V8** with a usable license and a supported CPU/firmware, for simulation
- .NET 10 SDK and .NET Framework 4.8 reference assemblies
- PowerShell 7, Python 3.13 and Node.js 24 for the development checks

From the repository root:

```powershell
.\scripts\Probe-Environment.ps1
.\scripts\Build.ps1
.\scripts\Test.ps1 -SkipBuild
```

If the probe reports missing prerequisites, follow [environment diagnostics](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/environment-diagnostics.md).

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

## Next steps

- Make your first change with the [engineering workflow](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/engineering-workflows.md).
- Browse a project in the [Project Explorer](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/project-explorer.md).
- Run PLCSIM tests with [simulation](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/simulation.md) and [block testing](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/block-testing.md).
- Ask the server what is supported: `feature_catalog` and `target_capabilities`.
