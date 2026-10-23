# Local protected credential references - 17 October 2026

The Windows Host now has local create/inspect/list/retire commands, hidden console
or explicit raw stdin input, context-bound public references and current-user DPAPI
storage with restricted ACLs. An internal synchronous consumer receives temporary
bytes that are cleared on success/failure. There is no plaintext-export command,
MCP credential method, native provider or external password/revocation operation.

## Exact source and attempts

- Base commit: `415ac97b14db3feb0efa3d6e6db59b3ddd4f4e70`.
- Frozen staged tree: `d9c418e71793a84c9c49d9e2b990e97a809f3fb1`.
- 1,925 source files; source archive SHA-256:
  `37836ce88c0552f7ccf32efc16334f712b0e7f9780f075f175ee181f714ecc82`.
- Identical archives/manifests retained under `artifacts/credentials-01` and
  `artifacts/vault-01`; their `source` directories are byte-identical before tests.
- Manifest SHA-256:
  `18ba5cb360b21159c63ce84f17ed75f7963fa6872e68b6ad8da9da3e60ae5694`.
- Production source/tests/contracts/dependencies stay frozen. Final edits are this
  record, STATUS, execution queue, a guide clarification about observed Windows
  package filesystem redirection and CI upload selectors for the new public test
  receipts. Workflow execution commands and runtime files stay identical.

The first full aggregate under `credentials-01` exited 1: 18 existing component
source-reuse tests refused their synthetic fixtures because the enclosing path
contained `credential`. Its failed result/log remain retained. The existing guard
is unchanged; the complete identical source was extracted under neutral `vault-01`
and the full aggregate rerun there. The separately running package process was
not restarted and completed from the original identical snapshot.

## Executed targeted and package checks

The managed suite passes 55 checks using actual Windows current-user DPAPI/ACLs.
Coverage includes randomized independent generations, exact bytes up to 65,536,
cleared borrowed arrays after successful/throwing consumers, purpose/subject/owner/
authority mismatch, invalid identity/Unicode/length, separate-process reopen and
locking, rehashed ciphertext and context tampering, duplicate/surplus JSON, hard
links, traversal, unsafe file/directory ACLs and refusal without automatic repair.
Missing header/lifecycle, foreign declared owner, relocated store and unknown
lifecycle cannot authorize use. Local retirement persists/idempotently disables
one generation while retaining its immutable ciphertext and other references.
Orphans count toward bounded store capacity. No plaintext sentinel appears in any
retained vault file. Foreign declared-owner refusal is not a different-user test.

The actual CLI harness passes 82 checks across 21 processes, both against the
managed output and Unicode-relocated portable Host: strict public schemas, no
plaintext/base64 secret in output, explicit redirected-input mode, rejected secret
argument/options, malformed/duplicate/missing public JSON, UTF-8 BOM support, exact
binary input, independent generations, current-user readability, list recovery,
wrong-context refusal, no export command, retirement/restart and missing lifecycle.
Rejected input publishes no credential generation. Private synthetic stores and
ciphertext hashes are recorded in the harness receipts; plaintext test secrets
are not retained. The standard test suites use no real credentials or endpoints.

The actual relocated Host was also exercised through a Windows pseudo-terminal.
Creation exited zero without echoing typed synthetic input; Escape exited 1 with
`CredentialInputCancelled` and published no extra generation. Independent DPAPI
readback matched the exact synthetic bytes. The verified receipt is
`artifacts/credentials-dev/interactive-console-verified.json`, SHA-256
`fd801bda99b2e5170e3df4e95105848181f42e10e80ce4cf29506fc1226e7a69`.

An earlier independent PowerShell probe used the logical local-data path rather
than its package-redirected physical path. Decrypt refused, and a non-terminating
PowerShell error incorrectly let that probe write a success receipt. That receipt
`interactive-console.json` is explicitly invalidated by
`interactive-console-correction-01.json` and must not be used. The corrected probe
stops on errors and uses the independently observed canonical path. Production
Host inspection had already succeeded; no production code change was required.
Physical-console human acceptance and different-user/profile-loss tests remain open.

All 19 portable stages pass. Original and relocated bundles contain 1,371 manifest
files with no unexpected files. The added Microsoft ProtectedData 10.0.13 package
has exact retained NuGet metadata, MIT license and upstream notices. Vendored
bytes, including original line endings/trailing blank line, remain unchanged per
`.gitattributes`; authored-source whitespace checks pass separately.

## Full aggregate and retained receipts

The neutral-path aggregate exited zero: all 38 managed suites, 1,280 Python tests
with one unavailable-native-evidence skip, 24 Explorer model/client checks and
all MCP workflows pass. Catalog/bindings stay at 197 static tools, 55 mutation kinds
and 270 features; normal Host discovery exposes 177 tools. All 106 missing historical
evidence rows/hashes are preserved. No native qualification was promoted.

Aggregate timing: `2026-10-17T01:11:54.4555572Z` to `2026-10-17T01:21:21.5668904Z`.

| Receipt (repository-relative) | SHA-256 |
| --- | --- |
| `artifacts/vault-01/source/artifacts/checks/result.json` | `6878844f7367774e87203c64d2f50009e1c791327b625f5dcb3b666938ced796` |
| `artifacts/vault-01/source/artifacts/checks/credentials/verification.json` | `e61d0353f0cd081e64d159f636da510f8d83daf330f09da85c648deda7038858` |
| `artifacts/vault-01/source/artifacts/checks/credential-cli/verification.json` | `f47aeb7b5570c7cff28a689c99175041c735d981c507e756c7baa1ba6353eee6` |
| `artifacts/credentials-01/source/artifacts/package-checks/result.json` | `a0ac74d54a0eb7f8f926a2cf28433be4567334d84419219632de20abcc4c66f9` |
| `artifacts/credentials-01/source/artifacts/package-checks/credentials/verification.json` | `e61d0353f0cd081e64d159f636da510f8d83daf330f09da85c648deda7038858` |
| `artifacts/credentials-01/source/artifacts/package-checks/credential-cli/verification.json` | `b6fe7ee66bec968aa03bb60c43afa118904cc8e3a7826a191c697cdc2f07a4a2` |

Portable ZIP: `artifacts/credentials-01/source/artifacts/releases/tia-mcp-offline_20261017_011007_a7a76116.zip`.
SHA-256: `60ee03f69b914100e95b02840be5e08b7b0b86db733301c930f4d7f20acbf017`.
Bundle manifest: `0301617d0ce212a35320028da4869f02f863bb49dd035e5a55f7a4f11a4b1c20`.
Hashes establish retained byte consistency, not publisher authentication.

The full aggregate command was `./scripts/Test-Offline.ps1 -OutputDirectory
artifacts/checks` from `artifacts/vault-01/source`; the package command was
`./scripts/Test-OfflinePackage.ps1 -OutputDirectory artifacts/package-checks`
from `artifacts/credentials-01/source`, both with the repository Python environment.
Both processes exited zero. Full console logs remain beside their source snapshots.
The first aggregate failure remains separate and is not relabeled as a pass.

CI upload selectors now retain only the two new public `verification.json` reports
for each managed/package job. Private vault files are not included. These additions
change no job command, dependency or test; selector checks cover existing receipts.


## Limits and continuation

The previous base passed hosted Python/.NET `38988463048` (all four jobs) and
Explorer `38988463046`. New-commit hosted outcomes are recorded separately.

Local availability does not prove endpoint authorization, password/key recovery,
certificate trust, external rotation or native admission. No generic reference is
accepted as scratch simulation metadata, and existing scratch credentials remain
separate. Expected bindings must come from a trusted authenticated provider before
any future consumption. Profile/key loss, packaged/unpackaged execution-context
migration, native provider handoff, secure native transport and independent external
state/recovery remain WA-09 obligations. DPAPI/ACLs do not defend against the account
owner, administrator or equivalent same-user code; no anti-rollback hardware claim.

Read the [workflow and recovery boundaries](../reference/protected-credentials.md)
and [full remaining queue](../plans/offline-execution-20261016.md). No Siemens SDK,
TIA, PLCSIM, physical endpoint or native release acceptance was available. This
increment does not complete WA-09, D12/X03 or the full goal.
