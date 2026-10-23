# OPC UA offline Host delivery — 16 October 2026

The existing parser and dependency analyzer are now exposed through eight production Host MCP tools: inspection, comparison, explicit dependency sets, set comparison, report paging, reopen, discovery and artifact download. The tools use committed uploads and immutable Host-owned handles. They do not depend on an engineering or simulator worker. The [workflow reference](../reference/opc-ua-workflow.md) describes the usable end-to-end interface; the [JSON Schema](../../contracts/opc-ua-workflow.v1.schema.json) defines its contracts.

## Delivered and verified

| Check | Result | Evidence |
| --- | --- | --- |
| Private Release Host build | Passed; zero warnings/errors | Command and binary hashes in delivery.json (`artifacts/opcua-delivery-20261016/delivery.json`) |
| Managed workflow harness | **24/24 passed** | workflow-managed.log (`artifacts/opcua-delivery-20261016/workflow-managed.log`) |
| Workflow JSON Schema tests | **6/6 passed** | workflow-schema.log (`artifacts/opcua-delivery-20261016/workflow-schema.log`) |
| Actual stdio production Host | **48/48 passed**, including explicit schema validation of every UA success/error response | mcp-02 verification (`artifacts/opcua-delivery-20261016/mcp-02/verification.json`) |
| Restart and native exclusion | Two real Host processes exited 0, neither forced; both configured workers nonexistent; zero worker exit receipts, no native operations | Same MCP verification |
| Upload transport retries in accepted MCP run | Zero | Same MCP verification |
| Initial standalone parser/dependency rerun | Snapshot refused concurrent watch source change before testing; retained as a failed snapshot, not a regression result | failed snapshot receipt (`artifacts/opcua-offline/c65662836d0c457c8a6dcd55bb41cb3d/result.json`) |
| Integration checkpoint `D:/TIAW/iq161/source`: isolated OPC UA package | **9/9 metadata, 31/31 parser, 27/27 dependency/CLI checks passed**, plus independent fixture impact and output-preservation checks; historical wrapper exit was 1 despite the passing receipt | [checkpoint receipt](<D:/TIAW/iq161/source/artifacts/opcua-offline/a03aca16124c40cc8fed0777a8c0ab2d/result.json>) |

Integration owns the final combined frozen-source build, portable aggregate and central CI. The parser **31** and dependency/CLI **27** checks were independently rerun successfully on the intermediate `iq161` checkpoint; this does not certify a later final checkout or the subsequently fixed runner. This handoff's successful managed and MCP builds are individually hashed, and do not assert that their shared Core dependencies are the final combined binaries.

### Intermediate checkpoint and wrapper exit correction

The isolated run under `D:/TIAW/iq161/source/artifacts/opcua-offline/a03aca16124c40cc8fed0777a8c0ab2d` completed at **11:02:33 Berlin / 09:02:33 UTC on 16 October 2026**. Its receipt records `status=passed`, all five implemented check groups passed, and `nativeAcceptance=notRun`. The log tails independently report 9 metadata, 31 parser and 27 dependency/CLI checks. Source-manifest SHA-256 is `e6891f10190f3615562751fb6f66a9695d6d44017c222974528ba321c4918e7e`; receipt SHA-256 is `5970bbc0e2ef15d6f3758b22ce2739b95fb6d41ee2f151debb7c1baccc792dfa`.

That run exposed an exit-propagation defect in `Test-OpcUaOffline.ps1`: its final CLI invocation intentionally returns a nonzero code to prove refusal to overwrite an existing report. The runner correctly checked that refusal and the unchanged report hash, then wrote a passing receipt, but left the expected native-process `$LASTEXITCODE` for the caller. Consequently the wrapper returned **1**. The historical nonzero wrapper exit is retained; it is not recast as a successful process exit.

Integration corrected the root runner by setting `$global:LASTEXITCODE = 0` **after** the outer `try/catch/finally`, so only a fully completed run clears the expected refusal code. Review confirmed that failures are still rethrown and a failed receipt write prevents reaching the reset. An isolated PowerShell control-flow probe with a prior exit value of 7 verified all three edges: successful body/finalizer reached the success marker and returned 0; a body exception returned 1 without the success marker; a finalizer exception also returned 1 without the marker. This is a review and targeted control-flow check of the correction, not a rerun of the entire corrected standalone runner. The final frozen-checkpoint validation remains integration-owned.

The tests independently check exact source/manifest downloads, content hashes and set digest, three breaking single-file changes, direct consumer effects, the two-edge Sensor → Telemetry → Reading impact, missing dependencies, duplicate-definition ambiguity, unchanged false completeness/compatibility flags, nested report sections, cursor continuity, explicit oversized-row omission and complete report retrieval. Upload release, restart discovery and retained graph integrity are exercised through actual MCP.

Negatives include malformed XML/JSON, external DTD rejection, a committed 16 MiB + 1 byte NodeSet, a 1 MiB + 1 byte consumer manifest, stale v1/v2 consumer bindings, stale cursor bindings, invalid section/kind/count/offset, path traversal and filesystem-path refusal, incomplete upload, committed upload overwrite refusal, independent immutable repeated publication, changed original source, changed intent manifest, receipt overwrite and missing completion receipt. The last two receipt cases are independently covered by the managed harness; the actual transport also covers missing receipt and changed manifest/source, including dependent-report refusal.

## Reproduction

Run from the repository root with .NET 10 and the declared Python test dependencies:

```powershell
dotnet build src/TiaMcp.Host/TiaMcp.Host.csproj -c Release --artifacts-path artifacts/opcua-delivery-20261016/build -m:1 -nr:false --ignore-failed-sources
dotnet build tests/TiaMcp.OpcUa.Workflow.Tests/TiaMcp.OpcUa.Workflow.Tests.csproj -c Release --artifacts-path artifacts/opcua-delivery-20261016/build -m:1 -nr:false --ignore-failed-sources
dotnet artifacts/opcua-delivery-20261016/build/bin/TiaMcp.OpcUa.Workflow.Tests/release/TiaMcp.OpcUa.Workflow.Tests.dll
python -B -m unittest discover -s scripts -p test_opcua_workflow_contract.py -v
python -B scripts/test_opcua_host_mcp.py --host artifacts/opcua-delivery-20261016/build/bin/TiaMcp.Host/release/TiaMcp.Host.exe --output <new-isolated-directory>
```

Use a new output directory for MCP evidence; the harness refuses to overwrite an existing one. Production `opcua_inspect` and comparisons always allocate a new publication, with intent first and completion receipt last. No tool accepts an output path or overwrite flag.

## Source handoff and binary identity

Owned implementation/contracts/tests are recorded in source-manifest.json (`artifacts/opcua-delivery-20261016/source-manifest.json`), SHA-256 `60305aa0e56962a5c9e6cb37b992211f85f70487c4b32bbe3544edf3ba532e35`. The manifest's scope is the UA lane, not the final combined source checkpoint.

| Accepted evidence binary | SHA-256 |
| --- | --- |
| Actual MCP Host DLL | `7466262fd89bdb614ee70c929894c633182b54190875e32939aa30a929a19680` |
| OPC UA DLL copied beside that Host | `481edaa984e4d2f1637c703e1799e07f2a39e8485857f41d119418e09fbff1c3` |
| Managed workflow test DLL | `01b3dcde6583db9edb01f923edab66a38886b4ce92273d5d92c678842aab8e10` |
| OPC UA DLL copied beside that managed harness | `94758f26b999eb92f4442945d31ea5487fb274e32ed5fa8fa0e093530ada1b9a` |

The private managed build occurred after the Host build during parallel work. Its dependency graph produced a different UA binary, so the two copies are identified separately. The final integration build must run both suites against its frozen graph. The delivery JSON retains all relevant copied DLL/EXE/PDB/runtime hashes rather than relying on an executable apphost hash alone.

Changed lane paths:

- `src/TiaMcp.OpcUa/OpcUaWorkflowStore.cs`
- `src/TiaMcp.Host/OpcUaTools.cs`
- `tests/TiaMcp.OpcUa.Workflow.Tests/Program.cs` and its new project file
- `scripts/test_opcua_host_mcp.py` and `scripts/test_opcua_workflow_contract.py`
- `contracts/opc-ua-workflow.v1.schema.json`
- `docs/reference/opc-ua-workflow.md` and `docs/reference/opc-ua-dependencies.md`
- This verification document and `docs/verification/opc-ua-native-eligibility-packet-20261016.md`

Integration owns the already coordinated Host reference/registration, solution, feature catalog, bindings, central runners/CI and summary documentation. Existing staged parser/dependency changes were preserved.

## Remaining scope

The [UA-03/UA-Q00 packet](opc-ua-native-eligibility-packet-20261016.md) is prepared for exact target binding and a future admitted worker implementation. No project/CPU or native session was selected or started here. Its first increment is bounded read-only eligibility plus one explicitly selected export; the wider import/generation packet remains separate.

Siemens mapping dialects, exact native profile admission, native writes/preservation/reopen/recovery, security configuration, endpoint/runtime validation, model-version compatibility and full client compatibility remain unqualified. Uploaded content provides no native freshness. Parser, schema and offline MCP success do not close those gates.
