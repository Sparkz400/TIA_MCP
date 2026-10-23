# Supplied web application review - 17 October 2026

Two offline Host tools now compare supplied application configuration and original
ZIP contents for an explicitly selected application under one exact saved owner.
Existing PackageStore extraction supplies bounded path/link/CRC/collision checks;
there is no second ZIP parser. Exact roots/default files, unsupported bytes,
public application/settings/generation declarations and immutable originals are
available through bounded pages and artifact downloads. Native compatibility,
generation, deployment and endpoint acceptance remain separate open obligations.

## Exact source

- Base commit: `fc0db1493320e4be81c7d672e9bc8da07ea1f174`.
- Frozen staged Git tree: `5cf26893ecfb55f634d07885fd64d5a677b8bc59`.
- Source: `artifacts/webapp-01/source`, 1,909 files; `source.zip` retained.
- Source manifest: `artifacts/webapp-01/source-manifest.json`, SHA-256
  `b6b774c4c979d445535fa3c3b3c68e9030151798766818af2e8ccd08e3a84199`.
- Only this record, STATUS and the execution queue are updated after freeze.
  Production source, tests, schemas, catalog, bindings and workflow/expansion guides
  stay identical to the tested source.

## Executed checks

The managed suite passes 108 checks. Independent fixtures establish changed/added/
removed/unchanged file hashes, exact default-file bytes, root and nested-content
separation, opaque binary/script preservation, application rename/peer preservation,
generation-property differences and distinct large numeric declarations. Missing
archives or changed source purposes never become a file-deletion report. Incomplete
inventory, missing/default application states and conflicting supplied settings
remain explicit. Application row order does not create semantic changes.

Adversarial inputs cover malformed UTF-8/JSON, duplicate fields/application IDs/
properties, wrong owner, virtual owner, unknown fields, wrong expected hashes,
default-page case/path/URL aliases, Windows traversal/device/stream/normalization
paths, ZIP duplicate/case/file-directory collisions, symlinks, CRC corruption,
entry/depth/input/expansion limits and malformed archives. Rejected archives leave
the complete immutable-review file/hash set unchanged. Transient extraction is
cleaned after successful and failed inspection; concurrent extraction refuses.

Retention checks cover exact originals, repeat requests, new-store reopen, released
source uploads, every file page, oversized-row omission, quota including incomplete
files, corrupted request/report/ZIP/saved source and a rehashed forged deployment
claim. Report recomputation rejects forged evidence. No script or browser executes.

The actual MCP harness passes 77 checks over two clean Host lifetimes: discovery,
strict request/output schemas, exact source import, two original ZIP uploads,
independent difference oracles, all report sections, exact original downloads,
wrong input/ZIP refusal without publication, missing content, upload release,
restart and corruption/restoration. Native workers are deliberately absent.

The frozen aggregate exited zero: all 37 managed suites, 1,280 Python tests with
one unavailable-native-evidence skip, 24 Explorer model/client checks and every MCP
workflow pass. Catalog/bindings report 197 static tools, 55 mutation kinds and 270
features; normal Host discovery exposes 177 tools. All 17 portable stages pass;
original and Unicode-relocated bundles have 1,360 manifest files, no unexpected
files and native workers disabled.

Aggregate timing: `2026-10-17T00:19:26.6680564Z` to `2026-10-17T00:28:57.0133665Z`.

## Retained receipts

Paths below are relative to the frozen source directory.

| Receipt | SHA-256 |
| --- | --- |
| `artifacts/checks/result.json` | `19902b99f950241fb6c27654fb735dd70af786f8c5aaeeaa792394838032b885` |
| `artifacts/checks/web-applications/verification.json` | `2377e2d0a984e778069b010531b6d7ca4bf194c5e9110ce7ff9d3ea38e0cde32` |
| `artifacts/checks/web-application-mcp/verification.json` | `d65dd5c5f09f28b7f62e766fd54041c66c5470e68e843c606d072bd56bb9a49f` |
| `artifacts/package-checks/result.json` | `b444740d00df8a134213554dffaf6fcbd275a2bc9036ab6bb2a7395b3c394f99` |
| `artifacts/package-checks/web-applications/verification.json` | `2377e2d0a984e778069b010531b6d7ca4bf194c5e9110ce7ff9d3ea38e0cde32` |
| `artifacts/package-checks/web-application-mcp/verification.json` | `98cd19634280913a7ab9b1b7854023d31935b3a17de53362c29f7217596e784f` |

Portable ZIP: `artifacts/releases/tia-mcp-offline_20261017_001957_ddbdb4b2.zip`.
SHA-256: `75a0f314d8f54a4ad165b20e342a4f75ddabcbffefc1d0f973f4fbb7d804f1c9`.
Bundle manifest: `6992816aa20fec56d815ddeb79c43f474fbcc735c48dd626b3e7aa8046885db8`.
Hashes establish byte consistency, not publisher authentication.

Executed with local Python dependencies from the frozen source:

```powershell
./scripts/Test-Offline.ps1 -OutputDirectory artifacts/checks
./scripts/Test-OfflinePackage.ps1 -OutputDirectory artifacts/package-checks
```

Both commands exited zero. Console logs remain beside `source`; detailed logs and
binary hashes are inside it. Expected refusal output is not an aggregate failure.


## CI, qualification and continuation

The base certificate commit passes hosted
[Explorer](https://github.com/Sparkz400/TIA_MCP/actions/runs/38984574381) and all
[Python/managed/package/OPC UA jobs](https://github.com/Sparkz400/TIA_MCP/actions/runs/38984574415).
Those results do not establish a later commit's hosted status. GitHub had no open
issues or PRs; only the primary main branch/worktree was active.

The report's seven native/authenticity/association/compatibility/generation/
deployment/endpoint flags always remain false. Public property labels are opaque;
they do not implement target-specific setters or generation semantics. Files are
retained untrusted, not sanitized or certified executable. Nested ZIP validation
also applies to downloadable ZIP assets. The operation-owned review boundary is
separate from generic uploads, which retain bytes before review validation.

WA-09 protected references and external-resource recovery are next. Source inspection
confirmed local path reservations and scratch-only credential binding. Three bounded
Context7 queries retrieved .NET DPAPI/ACL guidance; an official
[file-creation API reference](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemaclextensions.create?view=net-10.0)
resolved restricted security settings at creation. Existing-directory creation does
not repair existing permissions, so reopening must independently verify them.

A separate synthetic Windows probe passed current-user DPAPI roundtrip, wrong-entropy
and modified-ciphertext refusal, and restricted ACL-at-creation checks. Only encrypted
random fixture bytes were persisted; plaintext buffers were cleared. This ran in
PowerShell's .NET 10.0.12 runtime, not a production Host credential flow. Cross-user/
profile-loss recovery, general protected storage and external-resource reconciliation
remain unimplemented/unverified. Notes and receipt:
`artifacts/webapp-dev/wa09-research.json`, `artifacts/webapp-dev/wa09-probe/result.json`.

The 106 unavailable historical evidence rows retain prior hashes. Genuine Siemens
package/profile compatibility, generated DB preservation, native settings, protected
resource recovery, deployment and independent browser/endpoint acceptance remain
open. No backend-only test constitutes native or release qualification. Full D12,
X03, C03 and the complete 44-workstream queue remain open.

[Workflow](../reference/web-application-review.md),
[full D12/X03 plan](../plans/expansion-plans/web-server-access-configuration.md) and
[execution queue](../plans/offline-execution-20261016.md).
