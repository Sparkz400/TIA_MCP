# Publication review — 23 October 2026

Follow-up to the [18 October history audit](repository-history-audit-20261018.md)
before making the repository public. This changes the current tree only; Git
history still contains every removed file until a public copy is cut from a
clean snapshot or the history is rewritten.

## Siemens material

Removed from `fixtures/testsuite/native-owned-v21/`, with their hash rows kept and
listed under `notRedistributed` in its [provenance](../../fixtures/testsuite/native-owned-v21/provenance.json):

- three installed V21 Test Suite help pages (`inputs/132400841739.htm`,
  `inputs/154277293323.htm`, `inputs/vendor-help-style-setscope.htm`) and the
  byte-identical evidence copy `evidence/ts02/run-style-group-availability-01/source-vendor-help-style-setscope.htm`;
- `inputs/vendor-v17-page-26.png`, page 26 of the Test Suite Function Manual
  A5E50009374-AA.

No test read these files. Retained deliberately: the 352-byte and 625-byte
`vendor-help-example.tat`/`.tst` syntax examples and their native exports. The
`.tst` is hash-pinned evidence for the genuine `FOLDER_PATH` export loss, and
replacing it would require a new native capture. Exported definitions keep the
header that Test Suite writes into every export.

## Machine identifiers

Left unchanged. The 100 files containing local paths name only checkout roots
(`D:\Entwicklung\TIA_MCP`, `D:\03_Entwicklung\TIA_MCP`) and one VM image path;
no Windows user profile path is tracked. Most occurrences are raw receipts whose
bytes are hash-referenced. The `PCNICO` host name appears in two verification
headings. `scripts/Enable-GuestAccess.ps1` keeps its VMnet8 address and guest MAC
defaults, which only match the original VMware guest.

## Commit identity

217 earlier commits use a personal email address. This checkout now commits with
the GitHub noreply address. Earlier commits keep the old address unless the
history is replaced.

## Secret scan

Gitleaks 8.30.1 (archive SHA-256 `d29144de…afc4e`, matching the recorded release
digest), using the [18 October options](repository-history-audit-20261018.md#reproduction-and-retained-evidence),
scanned 236 commits (66.6 MB) at `6ec5153`. Exit code 1 with 72 generic-key findings:
the 69 already triaged plus 3 occurrences of the same `idempotencyKeyHash`
SHA-256 value in `fixtures/libraries/observation-contracts/synchronization-change.json:469`
(commits `d4a7693`, `938c58a`, `9d38c26`). That value is a SHA-256 hash, not a credential. No
credential was found. Output is in ignored `artifacts/repository-audit-02/`.

## Public snapshot

The public repository starts from one commit whose tree is byte-identical to a
commit of the private history; its commit message names that source commit.
It was published as `Sparkz400/TIA_MCP` on 23 October 2026; the private repository was
renamed `Sparkz400/TIA_MCP-private`. Private vulnerability reporting, secret scanning, push
protection, Dependabot alerts and security updates are enabled, and a ruleset blocks
deleting or force-pushing the default branch. Gitleaks on the public history reported only the
70 already-triaged generic-key matches. Markdown links into the untracked `artifacts/` folder
and to private pull requests, Actions runs and commits were turned into plain text. The
private repository keeps the full history and original evidence. Commit hashes,
Actions runs and pull requests cited in these documents refer to that private
history.
