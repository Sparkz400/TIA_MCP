# External-resource journal extension - 17 October 2026

WA-09 now has a portable external-resource portion in the existing engineering
operation journal. Exact provider/authority/owner/resource keys, independently
retained baseline and desired-state observations, separate success gates and
per-resource reconciliation prevent a local project restore from clearing an
unresolved external effect. Existing operation status exposes those records.
There is no new mutation tool, native provider or protected credential vault.

## Exact source

- Base: `05d083d2635a783b2e1a72bd73f7bcb4cffd269a`.
- Frozen staged Git tree: `59f59c0e230952ccc84e379a7d739243258033f7`.
- Source: `artifacts/external-01/source`, 1,914 files; `source.zip` retained.
- Manifest: `artifacts/external-01/source-manifest.json`, SHA-256
  `d4fc3e43c7e7cbef807bc6720a7a989be7dd3cb9975d75948e1ef3fa3dde1525`.
- Only this record, STATUS and the execution queue are updated after freeze.

## Targeted evidence

The managed extension passes 70 checks. Independent canonical-key/legacy-byte
oracles cover provider, authority, owner, resource and case distinctions;
malformed Unicode is refused before serialization can replace it. Separate
processes establish owner/child exclusion across projects and continued ownership
after reply loss. A synthetic child changes its own public-state file after
persisted dispatch intent and exits 17. Exactly one child mutation is retained;
retry/stop cannot erase uncertainty.

Checkpoint/outcome/reconciliation observations reject wrong operation, plan,
revision, resource, phase, state, surplus/duplicate fields and project-only proof.
Historical baseline/provider-byte or successful-outcome corruption refuses reopen.
Unknown/unreadable observations retain ownership. Local recovery admission and a
completed recovered copy still leave the external resource unresolved. Separate
known-absence or baseline readbacks resolve their own obligations. Reconciliation
preserves original failure/diagnostic history and never claims an engineering pass.

The actual MCP harness passes 44 checks over two clean Host lifetimes: existing
tool discovery, legacy/reconciled/succeeded/pending status, strict journal and
observation schemas, forbidden qualification claims, wrapper/provider-byte
corruption, restoration and byte-identical status after restart. All journal,
observation and synthetic-provider bytes remain unchanged; no native worker runs.

Bidirectional compatibility evidence is retained at
`artifacts/external-dev/compatibility.json`: the new binary reads three prior
terminal journals with their files unchanged, and the previous binary reads the
new local-only record. The additive null field is omitted rather than changing
old plan/record serialization. This check uses retained binaries/records from the
previous web-application validation, not reconstructed legacy fixtures.

## Full validation

The frozen aggregate exited zero: all 37 managed suites, 1,280 Python tests with
one unavailable-native-evidence skip, 24 Explorer model/client checks and every MCP
workflow pass. All 18 package stages pass. Original and Unicode-relocated bundles
contain 1,363 manifest files with no unexpected files. Catalog counts remain 197
static tools, 55 mutation kinds and 270 features; normal Host discovery exposes
177 tools. All 106 unavailable historical evidence rows/hashes remain preserved.

Aggregate timing: `2026-10-17T00:43:42.8817100Z` to `2026-10-17T00:53:13.2167580Z`.

The following receipts are relative to the frozen source directory:

| Receipt | SHA-256 |
| --- | --- |
| `artifacts/checks/result.json` | `d816eaea3e3962e036ed9e59a2bb70959fe533a7034591abc91e08d898c5ee7d` |
| `artifacts/checks/external-resources/verification.json` | `d5fadcb442859964581fe3d145e1fde002adddee369197b8cb5e0e2aad686831` |
| `artifacts/checks/external-resource-mcp/verification.json` | `c709de2115290cf47e0064ba55fb3cd909146df4517f5c6ce6b3368e8fb2f440` |
| `artifacts/package-checks/result.json` | `63be772ddd08017fc03a551d3c796050be0c9f151aa385cec5e95acaf2dfba1b` |
| `artifacts/package-checks/external-resources/verification.json` | `d5fadcb442859964581fe3d145e1fde002adddee369197b8cb5e0e2aad686831` |
| `artifacts/package-checks/external-resource-mcp/verification.json` | `d38a035d2ce974a8d6c3ecc396982e1f3fca9be3b737f988c7280c666fe7cbd5` |

Compatibility receipt SHA-256:
`f9201ff0d6f0a2342659f5c1619aaf506d681c7a17dd41cd902dc8ade5abcccd`.

Portable ZIP: `artifacts/releases/tia-mcp-offline_20261017_004404_505c2445.zip`.
SHA-256: `dac505e432b0904b79eae74f425ea7e80c0fa621c58b62e730af1426dbab3208`.
Manifest: `b76f34e69b75fc35955984a4818854f4321532e503aac76fb06e8477b7bbc476`.
These hashes establish retained byte consistency, not publisher authentication.

Executed from the frozen source with the repository Python environment:

```powershell
./scripts/Test-Offline.ps1 -OutputDirectory artifacts/checks
./scripts/Test-OfflinePackage.ps1 -OutputDirectory artifacts/package-checks
```

Both processes exited zero. Console logs remain beside `source`; detailed logs,
fixtures and binary hashes remain inside it. Expected refusal output is not a
failed aggregate. Documentation links add no new failures; seven historical
missing-evidence links remain unavailable.


## Limits and continuation

The previous base commit passed both hosted workflows: Python/.NET `38986615802`
and Explorer `38986615871`. New-commit hosted results are recorded separately.

This implementation verifies context and retained public bytes supplied by a
trusted internal adapter seam. It does not authenticate a provider, normalize
physical endpoint aliases, acquire external state, restore passwords/private keys
or prove certificate trust, UMC synchronization, live deployment or endpoint
reachability. Ownership coordinates only users of the same configured artifact
store. A future provider must enforce authenticated identity, state semantics and
native mutation admission. There is no exposed generic external dispatch API.

The Windows DPAPI/ACL research remains a synthetic platform probe, not production
protected storage. General credential references, secure local input, lifecycle,
owner/profile loss and native provider acceptance remain WA-09 work. No Siemens
software, SDK, PLCSIM, physical endpoint or native release acceptance was available.
The full [execution queue](../plans/offline-execution-20261016.md) remains open.
