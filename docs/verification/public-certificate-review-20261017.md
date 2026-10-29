# Supplied public certificate review - 17 October 2026

Two offline Host tools inspect one supplied public certificate per before/after
phase and retain immutable reviews against an exact saved project/hardware owner.
Public DER/PEM identity, supplied store/usage association, explicit UTC validity,
subject/issuer, public key and bounded extensions remain separately observable.
Matching fingerprints and parsed metadata never establish trust or native eligibility.

## Exact source

- Base commit: `563bd1756b0965090b0ea09839c3d3dae376e9ba`.
- Frozen staged Git tree: `c8d473ecc6d16d0b6a0d37fb85cfe9ff51fcf198`.
- Source: `artifacts/certificate-01/source`, 1,900 files; `source.zip` retained.
- Source manifest: `artifacts/certificate-01/source-manifest.json`, SHA-256
  `310ac6522aa25208d67478057cb2b03593438c8251a9d0302847c0e668ba793e`.
- After freeze, only this record, STATUS and the execution queue are updated.
  Production code, schemas, tests, catalog, bindings and workflow/expansion guides
  remain identical to the tested source.

## Executed validation

The new managed suite passes 96 checks. Independent generated RSA/ECDSA public
fixtures cover fingerprints, subject/issuer, UTC boundaries, SAN, KU, EKU, basic
constraints and opaque critical extensions. Association drift, unavailable content,
altered signatures, exact retention, restart, quota, paging and corrupt/forged
records are covered. All nine native/authenticity/trust flags remain false.

Malformed DER, duplicate/excessive extensions, oversized SAN/EKU/DER, multiple
certificates, private-key PEM prefixes/suffixes, PKCS12, PKCS7 and trailing text are
refused before operation-owned retention; the complete retained-file hash map stays
unchanged. Disposable test keys exist only in memory. The production tool takes
direct public input, avoiding generic upload retention before validation.

An initial fixture run used an unsupported single-certificate PKCS7 export; the
fixture now uses collection export. A later adversarial test reproduced .NET's
acceptance of extra ASN.1 fields inside Certificate and TBSCertificate. Explicit
envelope/RDN/extension parsing now rejects those fields before the platform loader.
The final checks retain this regression; no complete RFC policy validator is claimed.

Actual MCP passes 68 checks over two clean Host lifetimes, including strict schemas,
source import, exact owner selection, DER/PEM equivalence, malformed input with no
retention, missing phases, paging, upload release, restart and corruption/recovery.
The same harness passes in the relocated portable package. No native workers start.
Two copied access-store quota messages also now name public access review correctly;
access behavior/codes are unchanged and its existing suites run in both aggregates.

The frozen aggregate exited zero: all 36 managed suites, 1,280 Python tests with
one unavailable-native-evidence skip, 24 Explorer model/client checks and every MCP
workflow pass. Catalog/bindings report 195 static tools, 55 mutation kinds and 268
features; normal Host discovery exposes 175 tools. All 16 portable stages pass;
original and Unicode-relocated bundles have 1,356 manifest files, no unexpected
files and native workers disabled.

Aggregate timing: `2026-10-16T23:52:38.4104924Z` to `2026-10-17T00:01:53.5509082Z`.

## Retained receipts

Paths below are relative to the frozen source directory.

| Receipt | SHA-256 |
| --- | --- |
| `artifacts/checks/result.json` | `f5fa7e6363aea34af9f273850716b0fcbf6e28178484013a97e24bd9cea479ad` |
| `artifacts/checks/public-certificates/verification.json` | `679cfd93abe8dcbb4d247e4215b6228d2c7ea4783b47ed32c58e0ec2788b539f` |
| `artifacts/checks/public-certificate-mcp/verification.json` | `03f6bc1a018e1412334a423d7199de0660b6cfd6c6af7aac91abd3ef5923d7ac` |
| `artifacts/package-checks/result.json` | `213049cae02a350fff611352f488fbe2b13d615293df49e5d1f6f2ec143e21ff` |
| `artifacts/package-checks/public-certificates/verification.json` | `679cfd93abe8dcbb4d247e4215b6228d2c7ea4783b47ed32c58e0ec2788b539f` |
| `artifacts/package-checks/public-certificate-mcp/verification.json` | `1dc67e6ec2acc5d9ee322d2505abd556a4fa69d45ab4f5134f5aedb6eb9d9583` |

Portable ZIP: `artifacts/releases/tia-mcp-offline_20261016_235308_44b7d404.zip`.
SHA-256: `2a221413d5e1c56aadaf7611b4b920e3fe7e963a7c52b5da6e8443853d30f06b`.
Bundle manifest: `28ffc6a442cb7959ac8cd1b015a574d0e5a45db6eefcc05986f1097cb9249f21`.
Hashes establish byte consistency, not publisher authentication.

Executed with local Python dependencies from the frozen source:

```powershell
./scripts/Test-Offline.ps1 -OutputDirectory artifacts/checks
./scripts/Test-OfflinePackage.ps1 -OutputDirectory artifacts/package-checks
```

Both commands exited zero. Console logs remain beside `source`; detailed logs and
binary hashes are inside it. Expected refusal output is not an aggregate failure.


## CI, limits and continuation

The base commit passes hosted Explorer
and all Python/managed/package/OPC UA jobs.
These results do not establish a later commit's hosted status. GitHub had no open
issues or PRs; only the primary main branch/worktree was active.

This workflow does not build chains, contact endpoints, consult trust stores,
verify signatures/revocation, import keys, assign certificates or mutate native
configuration. Unknown extension/identifier content is not secret detection;
callers must supply public data only. Client/transport logging is outside the
operation-owned retention boundary. Native ownership, usage policy, compatibility,
key availability and physical endpoint acceptance remain unverified.

The next executable work is WA-04 app/archive/configuration review and WA-09
external-resource recovery. Source inspection identified existing PackageStore
intake, bounded ZIP validation and retention as the reuse route. A separate actual
Host probe preserved three supplied web files plus the original ZIP and identical
manifest pages after restart; no browser/script/native execution occurred. This is
reuse evidence, not a delivered app comparison or deployment workflow.
Notes and receipt: `artifacts/certificate-dev/wa04-research.json` and
`artifacts/certificate-dev/wa04-probe/result.json`.

The 106 unavailable historical evidence rows retain their prior hashes and remain
unverified. WA-08 native certificate lifecycle, full D12/X03/C03, the remaining
44-workstream queue and release acceptance stay open. Browser/UI acceptance was
not repeated for this backend-only increment.

[Workflow](../reference/public-certificate-review.md),
[full D12/X03 plan](../plans/expansion-plans/web-server-access-configuration.md) and
[execution queue](../history/offline-execution-20261016.md).
