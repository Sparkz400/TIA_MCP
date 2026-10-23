# Supplied alarm context in diagnostics - 17 October 2026

Existing diagnostic planning, watch planning and replanning now accept an explicit
alarm, supervision or HMI alarm from one exact saved review. The case retains the
selected object, captured ancestry and original direct relationship observations.
Watch planning revalidates that context against the retained review; replanning
requires explicit reselection or leaves the old context in predecessor lineage.
No tool, dependency or native operation was added.

This is supplied context, not native ownership or executable storage inference.
Block, instance and signal selection remain independent requirements. A supplied
relationship cannot establish that selected storage explains an alarm. Missing,
protected, ambiguous, unsupported and omitted evidence remains visible.

## Exact source

- Base commit: `19cfb5d997d39e4ce7bdd03230ae849ecb396aec`.
- Frozen staged Git tree: `4cb0d224427a6801960b7bc855231c2e1d644ebd`.
- Frozen source: `artifacts/alarm-context-01/source`, 1,881 files; original ZIP retained.
- Source manifest: `artifacts/alarm-context-01/source-manifest.json`, SHA-256
  `56ed8f1eb810a52a302c6f80c645e2d656c302996ebc33b0593320fcd6787279`.
- Final documentation edits are limited to this record, STATUS and the execution
  queue. Production source, schemas, tests, catalog, bindings and workflow guide
  remain identical to the frozen validation source.

## Executed validation

The 31 diagnostic groups pass, including 38 new supplied-alarm checks. They cover
exact selectors, equal alarm numbers under different owners, HMI/supervision
selection, protected metadata, missing parents/cycles, ancestry and byte limits,
original relationship retention, unresolved/mismatched/unsupported edges,
oversized selection refusal, explicit replan lineage and tampered retained context.
Missing executable storage still refuses watch planning. The first targeted run
passed 32 new checks; the final expanded targeted and frozen runs pass all 38.

Actual MCP passes 58 steps across four clean Host lifetimes, both in the frozen
aggregate and relocated portable package. Request/output schemas, stale selector
refusals, watch-envelope retention and explicit replan selection are exercised.
No native worker is launched.

The full frozen aggregate exited zero: 34 managed suites (including 154 Core
groups), 1,280 Python tests with one unavailable-native-evidence skip, 24 Explorer
model/client checks and all MCP workflows pass. Catalog/source binding checks
retain 191 static tools, 55 mutation kinds and 264 features; normal discovery
exposes 171 tools. Validation ran from 23:05:18 to 23:14:17 UTC on 16 October.

Portable validation exited zero after all 14 stages. Original and Unicode-relocated
bundles contain 1,347 manifest files with no unexpected files. A separate probe
opened a copy of the previous commit's saved diagnostics with the new packaged
Host: all 49 original files remained byte-identical, the watch-plan ID stayed
identical, and old cases/envelopes acquired no new optional field. The Host exited
normally without workers. Receipt:
`artifacts/alarm-context-dev/backward-01/verification.json`, SHA-256
`0095b49c18aa9dd59c7c8f483db38babeb6cee1b49bfe199e8aa1337f5fa5a57`.

## Retained receipts

Paths below are relative to the frozen source directory.

| Receipt | SHA-256 |
| --- | --- |
| `artifacts/checks/result.json` | `0cc92fb68c7b2dbc83667428b977018e848c35d42502ed62f68089de675bc2dc` |
| `artifacts/checks/diagnostic-mcp/result.json` | `75a216f2bac742adc36bd3dbb0c5dab2bd11db23b3522283ebf6fd7d401f860b` |
| `artifacts/package-checks/result.json` | `0130c32e300b73092f7ae475919c002c4222981e5d24512ea5eccd7e70407f0a` |
| `artifacts/package-checks/diagnostic-mcp/result.json` | `6d2f10737ee8b22a03e536adc81837a04257d2f2a66214924a0ba2edaf83a887` |

Portable ZIP: `artifacts/releases/tia-mcp-offline_20261016_230540_1844d20d.zip`.
SHA-256: `3d25326b152da3cb1ae2b45cb3fc688ad2035cd6713eb987b66b0e9420d76bd7`.
Bundle manifest: `d1b373b5b23ea61e24e0f13f92a9834f0895c837d47070d44332f38bd30a1bea`.
Hashes establish byte consistency, not publisher authentication.

Executed from the frozen source with local Python test dependencies:

```powershell
./scripts/Test-Offline.ps1 -OutputDirectory artifacts/checks
./scripts/Test-OfflinePackage.ps1 -OutputDirectory artifacts/package-checks
```

Console logs remain beside `source`; detailed logs and exact binary hashes remain
inside it. Expected negative/refusal output is not an aggregate failure.

## Qualification and continuation

The base commit passes hosted Explorer
and all Python/managed/package/OPC UA jobs.
These results do not establish a later commit's hosted status. GitHub had no open
issues or PRs at this audit; only the primary main branch/worktree was active.

AL-03/08 now has supplied diagnostic context composition. Exact member/instance/
parameter association, genuine PLC alarm formats and native ownership, complete
inventory/type capture, lifecycle/recovery, generated dependencies, placeholder
rules, HMI integration and runtime remain open. The full D11 scope is not complete.
Next independent work is the full D12 web/access plan and supplied configuration
review, keeping project UMAC, CPU runtime, legacy web, SIWAREX and OPC UA rights
separate. Research notes and source discrepancies are retained under
`artifacts/alarm-context-dev/d12-research.json`; they are not implementation proof.

No Siemens installation, native compile/import, physical device, browser/visual
acceptance or runtime test was used. The 106 unavailable historical evidence files
retain their prior hashes and remain unverified.

[Workflow](../guides/trace-diagnostics.md), [full D11 plan](../plans/expansion-plans/plc-alarms-prodiag.md)
and [complete execution queue](../plans/offline-execution-20261016.md).
