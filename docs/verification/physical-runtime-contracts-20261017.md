# Physical runtime protocol contract evidence — 17 October 2026

Baseline: `317b840` (physical runtime plan). Scope: pure Web API context/selection/request/response contracts, strict result schema, managed tests and independent Python output checks. No runtime MCP tool or physical adapter is exposed. Full RT-01, D16 and the 44-workstream goal remain open.

The focused build passes 93 managed checks. Four Python tests pass, and the independent schema/value/time check accepts four actual C# results. Tests verify exact UInt64/Int64 values, wrong connection/epoch/certificate rejection, read-request tampering, recursive duplicate properties, malformed/oversized response refusal, bounded raw bytes, permission separation, remote-error redaction and backwards UTC handling. Results do not authenticate any endpoint, establish data-type conversion, or prove live program identity.

Full frozen aggregate validation passed: 43 managed suites, the Python suite (1,459 discovered cases), Explorer model/client checks, contracts/source bindings and every configured MCP workflow. The 93-check runtime suite and independent Python cross-check over four actual managed results also passed in the aggregate. The separate portable package run passed all 25 stages, including strict original/relocated bundle checks and credential recovery. Native execution remains false.

Evidence under `artifacts/runtime-contracts-01`:

- `source-02/artifacts/aggregate/result.json` records the aggregate pass. Its source manifest SHA-256 is `b164d2e3927be6019468e8e1307bd9345b5219c210c8a426f35b8ca1d22696e9`.
- Final review caught regenerated bindings replacing 106 unavailable historical evidence hashes with missing/null values. Those entries were restored exactly from baseline; `historical-evidence-restoration.json` and `corrected-bindings-validation.json` retain the correction and successful validation. Historical evidence was not reexecuted.
- `snapshot-comparison.json` proves `source-03` differs from `source-02` only in that coverage metadata file. Production code, schemas, tests and runners are identical. Source-03 manifest SHA-256: `9eac90ac64891e79863cfc3e9cfab5732fa8755c13911e7f73ef151b68460607`.
- `source-03/artifacts/package-checks/result.json` records a fresh 25-stage pass with the restored metadata. Final bundle manifest SHA-256: `799541f766e421724a3b5133ffbc953324b86389c14e44cba5e8f5c02ef14657` (1,462 files). The earlier package pass remains retained, not substituted for this result.
- `python-discovery-count.json` records the independent test discovery count. PowerShell's transcript did not retain child-process output counts; the aggregate terminal receipt proves the suite passed, not that every discovered case executed without skips.

Final documentation edits summarize these terminal receipts. The final source comparison must allow documentation changes only relative to source-03. The test runners include the new managed suite and independent Python fixture check.

No PLC connection, TIA, PLCSIM, native build or browser operation was performed by this increment. The full engineering-workstation `Test.ps1` entry point was not run because its native prerequisites are unavailable. Existing inventory tools and simulator ownership remain unchanged. The prior hosted CI billing refusal is not an executed code failure; inspect the exact new commit's jobs after integration.
