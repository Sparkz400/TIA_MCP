# Inventory response isolation and snapshot identity — 16 October 2026

This increment covers D16/F02 using synthetic HTTP responses and in-process Host
calls. No Siemens installation, native worker, external controller or real TLS
endpoint participated. The older inventory capture report is historical; its
missing native receipts are not replaced by this run.

## Reproduced defects and repairs

- Null, scalar and array method results could throw `InvalidOperationException`,
  bypassing per-module failure handling. Browse, node and I&M results now validate
  their container shapes before accessing properties. Envelope version, exact
  response ID, duplicate properties, result/error exclusivity and error-code
  shape are checked. Incomplete HTTP reads become sanitized `OnlineUnreachable`
  failures; neither remote diagnostics nor credentials are copied into rows.
- Returned snapshot/page metadata shared a mutable warnings array. Returning a
  copy now protects the retained coverage from caller changes, just as serialized
  rows already protect their fields.
- Controller hardware IDs could be bound to a different row; a duplicate name
  could then satisfy the old name check. Controller row/ID consistency now works
  in both directions and null bindings refuse before opening transport.

No tool, schema, dependency, export column or native capability was added.

## Development evidence

Logs under `artifacts/inventory-response-dev/` retain three failed checks:
`before.log` reproduces the null JSON result crash; `metadata-before.log`
reproduces warning mutation after the response repair; `binding-before.log`
reproduces the missing pre-transport controller-ID guard. `final.log` passes
30,315 checks, including 30,000 rows over 221 bounded pages.

New cases exercise 24 malformed method payloads, six invalid envelopes, 401/403,
redirect refusal, a synthetic I/O exception, explicit retry, caller cancellation,
per-module and common-controller failures, stale-value clearing, immutable
ancestors, exact failure provenance and CSV/XLSX export of success/failure rows.
These are fake-handler/in-process tests, not network or physical-device proof.

## Frozen verification

Final source is `artifacts/inventory-response-02/source`, exported from staged Git
tree `27df4a6e0ce8e05981fccbecb1bf6ec03422793a`, based on
`6bf024e4c28f6bb8fa1cfd89f311a90bf8d4982e`, with 1,791 tracked files.
Source manifest SHA-256:
`5b38bba108493e34c59b0f4162b9b71c6aa4642d000e75a267b2b987ca27f0e2`.

`Test-Offline.ps1 -OutputDirectory artifacts/checks` passes all 29 managed suites,
1,273 Python tests (one unavailable-native-evidence skip), 24 Explorer model/client
checks and every portable MCP workflow. The frozen inventory suite passes all
30,315 assertions. Full console output is retained in
`source/artifacts/aggregate-console.log`; the final result is
`source/artifacts/checks/result.json`, SHA-256
`6d24a3a2b1df304b6596be5379efc2d32fdd60e4ba6fc4209cf7f0831723123b`.

Snapshot `inventory-response-01` was prepared but not executed. Snapshot 02 adds
one export change to read the defensive metadata copy once, avoiding an array
copy for every exported row. All non-documentation bytes match snapshot 02;
later updates are documentation only.

The catalog remains 175 declared tools, 55 mutation kinds and 248 features.
Six historical evidence inputs match; 106 remain unavailable. Inventory response
commit `d2b57ad` passes all hosted checks: Python, managed, package and isolated
OPC UA in `38936287994`; Explorer in `38936287930`.
No new portable ZIP, browser rendering, desktop client or clean destination
installation was tested locally for this increment.

## Remaining gates

D16/F02 remain open for broader provenance, ambiguity, export compatibility and
supported-adapter coverage. Real controller/module mapping, network/TLS/session
behavior, installed firmware, SDK compilation and hardware acceptance require the
actual products and permitted targets. Native qualification remains false.
