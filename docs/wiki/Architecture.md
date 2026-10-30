![An MCP client connects to the Windows .NET 10 Host, which supports portable document workflows and isolated workers for TIA Portal V21 and PLCSIM Advanced V8.](https://raw.githubusercontent.com/Sparkz400/TIA_MCP/main/docs/assets/architecture.svg)

## Components

- **MCP client** talks to the Host over stdio.
- **Host** (`TiaMcp.Host`, .NET 10, Windows) serves the MCP tools, portable document workflows and saved project reviews.
- **OPC UA client** supports bounded read-only scalar, array and matrix acquisition without TIA. An optional named client identity uses protected Windows per-user storage separately from retained session records; certificate persistence does not restore sessions. Bounded subscriptions can also record an explicit event notifier and selected signals, preserving original event fields, queue overflow and receipt-ordered timelines; see the [event profile](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/opcua-runtime-subscriptions.md#read-only-alarm-and-event-capture). Captures can explicitly opt into bounded recovery of the same live session, retaining downtime and uncertainty; Host restart never resumes acquisition. Retained captures can be [compared offline](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/diagnostic-ua-comparison.md). See the [runtime workflow](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/opcua-runtime.md) for setup, rotation, representations, limits and qualification boundaries.
- **Native workers** (.NET Framework 4.8) run each Siemens call in an isolated process:
  - `TiaMcp.Openness.V21` for TIA Portal V21
  - `TiaMcp.Simulator.V8` for PLCSIM Advanced V8
- **Protected credentials** stay in the Host's per-user store. A worker receives a secret only for one block protection or CPU protection-level apply, sealed to that apply; see the [credential route](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/protected-credentials.md#change-apply-credential-route).
- **TIA Add-In companion** (`TiaMcp.AddIn.V21`, .NET Framework 4.8) runs inside TIA Portal. It sends a project-tree selection to a Host that was started with explicit enrollment, over the Host's one-use pipe challenge. It reads only; one native handoff is verified (`local.addin.companion.20261029`). See the [Add-In companion](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/addin-selections.md#native-companion-build-publish-and-enroll).
- **Project Explorer** is a read-only browser view. It works as a standalone offline HTML snapshot or through a compatible MCP Apps host.

Siemens assemblies are loaded from your installed products and are never redistributed. The portable package disables native workers entirely.

## Changes are recoverable, not transactional

Native changes follow **prepare → preview → checkpoint → apply → verify**. A change can partially fail across Siemens calls; recovery uses its checkpoint and journal. The public simulation workflow works only on adapter-owned simulator instances: it can download a changed program online, set virtual time and inject interrupts there, but it does not expose physical PLC download, force or CPU control.

## Support claims

The diagram shows component boundaries, not support for every operation, target or environment. Offline tests, native compilation and runtime qualification are separate claims. Check:

- [Current status](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/STATUS.md)
- [Tool reference](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/tools.md)
- [Engineering coverage](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/engineering-coverage.md)
- `feature_catalog` and `target_capabilities` from the running server

Full detail: [system architecture guide](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/architecture.md).
