# Exact source reuse for standalone component qualification

Without `--reuse-source-report`, the qualifier authors a fresh simulation-capable
source and installs it; the captured storage profile then binds by complete
installed FB/UDT content equality. That route passed on 24 October 2026 with
native runtime acceptance; see [fresh installation](../verification/component-fresh-install-20261024.md).
Run it from a short checkout path (TIA limits project paths to 143 characters)
with a worker registered in the Openness AllowList.

`qualify_plc_component.py --reuse-source-report` uses the exact source archive
created by `component-native-04` in a **new consumer scratch project**. It never
runs source authoring, opens the old consumer, resumes its component operation,
or retrieves its checkpoint. The failed native04 result remains failed.

This closed qualification profile requires the retained native04 failure before
typed dispatch, the reviewed report SHA256, archive SHA256
`9d691a31d60863e7384928df09eb37345eb20bd791f18d919cf7960fbfc750cc`, and these exact
committed versions:

| Type | Type GUID | Version GUID |
| --- | --- | --- |
| TiaMcpComponentGate | f566f7e9-d5bc-415e-b552-4828e3b1027c | e129ccee-1eaa-456b-b2f7-1581fcc3b6b9 |
| TiaMcpComponentState | 35c9be87-892c-4ce6-a724-545ab1d41683 | eee41dd4-5137-48bb-a42b-d7fc64637eec |

Before any host/TIA startup, the helper verifies the report, durable plan/import/
component/failed-preparation receipts, raw source and full source-oracle pins,
dependency closure, current fixture contract and every fixture file. It copies
the archive and fixtures into the fresh run directory and verifies their hashes.
The original input hashes are checked again after cleanup. Reparse paths,
credential stores, changed inputs and existing staging destinations are rejected.

The existing consumer sequence still owns all installation acceptance:
exact library import; DB plus standalone FC/call/bindings; compile and save;
unrelated-content comparison; fresh reopen; recovery from the **new consumer's
first pre-import checkpoint**. There is no OB insertion or runtime action.
For the captured instance DB, fresh-reopen proof additionally compares every
raw XML byte after excluding only its exact direct native `DocumentInfo` span.
The declaration, namespace declarations, whitespace, comments and unknown XML
remain exact; both raw hashes and the payload hashes are retained.
Production combined apply remains disabled. A storage capture or successful
source-reuse preflight does not count as a complete installation pass.

Use a fresh output directory and a reviewed combined build that contains the
separately qualified exact storage profile. The report SHA below binds the
immutable native04 result, not the newly generated result:

```powershell
python -B <new-freeze>/scripts/qualify_plc_component.py `
  --host <new-freeze>/src/TiaMcp.Host/bin/Release/net10.0-windows/TiaMcp.Host.exe `
  --worker <new-freeze>/src/TiaMcp.Openness.V21/bin/Release/net48/TiaMcp.Openness.V21.exe `
  --artifacts D:/03_Entwicklung/TIA_MCP/artifacts/p15/n `
  --artifact-policy-root D:/03_Entwicklung/TIA_MCP/artifacts `
  --output D:/03_Entwicklung/TIA_MCP/artifacts/p15/component-native-05 `
  --reuse-source-report D:/03_Entwicklung/TIA_MCP/artifacts/p15/component-native-04/result.json `
  --reuse-source-report-sha256 5fe30a11336193dfdaf8b3ce8d7bcecd7b67fac2ecf7885f90d53e37ac529c0b `
  --timeout 600 --run-native
```

`--reuse-source-report` and `--recover-from-report` are mutually exclusive. A
lost response stops the attempt; there is no automatic authoring fallback,
mutation replay, installation continuation, or recovery dispatch.
