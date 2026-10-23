# Supplied public access review - 17 October 2026

Two Host tools now compare supplied public grant graphs and retain immutable reviews
against an exact saved project/hardware owner. User/group/role traversal, scoped
required/allowed-right comparison and before/after entity changes keep the five
security domains distinct. Missing entities, cycles, incomplete coverage and
unknown/disabled states remain explicit. No native authorization, lockout-safety,
minimum-rights or compatibility claim is made.

The [full D12/X03 plan](../plans/expansion-plans/web-server-access-configuration.md)
now covers the public API/target audit, monitoring and app-update scenarios,
certificates, generated web DBs, download callbacks, UMC, operational settings,
watch/display assignment and external-resource recovery. Supplied review does not
complete those native lifecycles or the full release obligation.

## Exact source

- Base commit: `52f9247b97cbdf8e971ba3a240581643665cf5a7`.
- Frozen staged Git tree: `8d590d5c8b0d69660fe189c6da662c8b0078517a`.
- Source: `artifacts/access-01/source`, 1,891 files; original `source.zip` retained.
- Source manifest: `artifacts/access-01/source-manifest.json`, SHA-256
  `aa82b855d578857615ab1b897055ff29329b02a69f788c0066545a559a6c50ef`.
- After freeze, only documentation changed: this record, STATUS, execution queue,
  and an explicit watch/display-assignment obligation in the full D12 plan.
  Production source, schemas, tests, catalog, bindings and workflow guide remain
  identical to the tested source.

## Executed validation

The new managed suite passes 73 checks: exact owner/domain/scoped rights, transitive
membership and unrelated-role exclusion, diamond/cyclic/missing/deep graphs,
unknown/disabled declarations, incomplete coverage, required/allowed consistency,
duplicate/malformed identities and fields, forbidden-field non-echo, UTF-8 and
collection bounds, exact retained bytes, paging/oversized rows, quota, corrupted
source/input/report and rehashed forged envelopes. Initial development runs exposed
fixture upload-API/JsonNode mistakes and an inconsistent copied store refusal code;
these were corrected before the final targeted run and source freeze.

Actual MCP passes 42 checks over two clean Host lifetimes. It exercises discovery,
strict request/output schemas, exact review import, public input retention, grant
comparison, full paging, domain drift and forbidden-field refusals, upload release,
restart and corruption/recovery. Both the aggregate and relocated package run the
same harness and pass. No native workers start.

The frozen aggregate exited zero: all 35 managed suites, 1,280 Python tests with
one unavailable-native-evidence skip, 24 Explorer model/client checks and every MCP
workflow pass. Catalog/bindings report 193 static tools, 55 mutation kinds and 266
features; normal Host discovery exposes 173 tools. All 15 portable stages pass;
original and Unicode-relocated bundles have 1,352 manifest files, no unexpected
files and native workers disabled.

Aggregate timing: `2026-10-16T23:27:54.3976708Z` to `2026-10-16T23:37:04.4914659Z`.

## Retained receipts

Paths below are relative to the frozen source directory.

| Receipt | SHA-256 |
| --- | --- |
| `artifacts/checks/result.json` | `44dd6965cb95e47bcba6a711b04e1cb86ce8223df1ca7659ca169d2399aaf8bd` |
| `artifacts/checks/access/verification.json` | `69ba384ac13b90f60e0ae471c5dc32da9ea21bc92943cf7503ed924eece8e1d2` |
| `artifacts/checks/access-mcp/verification.json` | `db353d6ad43b7ed32133859f4ad0d741d918bef45595f481a6c457729e965c65` |
| `artifacts/package-checks/result.json` | `2c3f449bd078b8268f382d627a8af067c4a2538d2fa91d6df7649c8907368d61` |
| `artifacts/package-checks/access-mcp/verification.json` | `d5b7ef03a338e0b8b5285cded1fe59c1439d0ebf7af0f196ac1e88f7d13b2dcc` |

Portable ZIP: `artifacts/releases/tia-mcp-offline_20261016_232823_1b7da6cc.zip`.
SHA-256: `000592aaac5dd8809643a88b3d017b0d74e4f03d227863ddcebf4eae368dfe47`.
Bundle manifest: `6379328a107b890c48e8afbd49991a29ca0febb1867cbd28029f968c92c103c5`.
Hashes establish byte consistency, not publisher authentication.

Executed with local Python dependencies from the frozen source:

```powershell
./scripts/Test-Offline.ps1 -OutputDirectory artifacts/checks
./scripts/Test-OfflinePackage.ps1 -OutputDirectory artifacts/package-checks
```

Both commands exited zero. Console logs remain beside `source`; detailed logs and
binary hashes are inside it. Expected refusal output is not an aggregate failure.

## CI, limitations and continuation

The base commit passes hosted Explorer
and all Python/managed/package/OPC UA jobs.
Those results do not establish a later commit's hosted status. GitHub had no open
issues or PRs at this audit; only the primary main branch/worktree was active.

The public-only request rejects secret fields but does not detect secrets disguised
as identifier text. Generic uploads retain bytes before this review validates them;
this is not redaction or protected credential storage. Supplied potential grants,
including disabled/unknown declarations, do not implement native deny/activation
semantics. Required rights not observed are not proof of denial; a clean comparison
is not minimum-rights or administrator-lockout acceptance.

Next executable work is WA-03 public certificate inspection, then WA-04 app/archive
review and WA-09 external-resource recovery. A separate .NET 10.0.13 probe established
that `X509CertificateLoader.LoadCertificate` accepts trailing bytes, concatenated
certificates and a certificate followed by a private-key PEM block while returning
only the first public certificate. PKCS12 refused. This determines a strict
single-payload intake requirement; it is not a shipped certificate parser.
The probe generated disposable keys in memory and retained no private material.
Receipt: `artifacts/access-dev/certificate-probe/result.json`, SHA-256
`6826daa4eab9bb819dc7a04dd41bc7c94f52ae05493c44978304b91af4b6bb66`.
Documentation/next-work notes: `artifacts/access-dev/wa03-research.json`.

Native capture, exact service/profile semantics, real credentials, certificate/app
operations, physical endpoint and browser acceptance remain unexecuted. The 106
unavailable historical evidence files retain their prior hashes and remain
unverified. Full D12/X03/C03 and the complete release queue remain open.

[Access workflow](../reference/access-review.md), [full D12/X03 plan](../plans/expansion-plans/web-server-access-configuration.md)
and [execution queue](../plans/offline-execution-20261016.md).
