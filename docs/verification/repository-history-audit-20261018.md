# Repository and history audit — 18 October 2026

Audited commit: `dab0104d83c14c12f25cdab79d58debb6985e279`. The
[machine-readable report](repository-history-audit-20261018.json) records the
scope, tool hash and every finding's disposition. This is a technical audit of
the available Git history, not public-release or native acceptance.

## Scope and results

All 106 commits reachable from the local refs were inspected: 5,042 reachable
objects, 3,440 distinct file blobs totaling 233,255,788 bytes, and 2,170 current
and historical paths. Every commit tree was enumerated, including all paths
sharing a blob. Reflogs, dangling objects, ignored local artifacts, remote refs
not fetched and future Dependabot branches are outside this snapshot.

Gitleaks 8.30.1 was downloaded from its official release; its archive SHA-256
matched GitHub's published asset digest. The default rules scanned all reachable
commit diffs, including root and merge-parent changes, with full redaction,
five decoding levels, three archive levels and no repository ignore file,
baseline or inline suppressions. The tool reported 106 commits and 35,075,380
diff bytes scanned. Its exit code was **1**, with **69 generic-key findings**;
the raw report has not been replaced with an empty report or a suppression list.

All 69 findings were checked against their original commit and line:

| Matches | Disposition | Evidence |
| --- | --- | --- |
| 28 | Assembly SHA-256 metadata | Exact 64-character `baseApiSha256` / `testSuiteApiSha256` fields in retained admission records. |
| 34 | Translation row identities | Opaque exported keys paired with their C-column worksheet cells, source cells and targets; the reader uses these as `TranslationRow.NativeKey`. They are not authentication tokens. |
| 7 | Prose or assertion text | Capability descriptions, synthetic API-shape documentation and the missing/duplicate row-key assertion. |

No credential was confirmed in these findings; none remains untriaged. This
does not prove absence of every secret. Scanner defaults still have their own
heuristics and built-in allowlists; binary image content is outside textual
credential detection.

Every reachable blob was also read for file signatures and NUL content. The
only detected binary is the 266,016-byte retained
`fixtures/testsuite/native-owned-v21/inputs/vendor-v17-page-26.png`. Visual
inspection identifies it as a Siemens Test Suite manual page with an XML
example, not a project or credential screenshot. No PE/ELF executable, ZIP/OLE
container, native project/archive extension, certificate/key file extension or
Git LFS pointer was found. These are stated signature/extension checks, not a
general-purpose forensic file-format detector.

No historical path falls under the inspected generated/cache directories
(`bin`, `obj`, `node_modules`, `artifacts`, `output`, `test-results`, `.vs`,
`.venv`). Retained fixture receipts and source snapshots do exist outside those
directories and are intentional evidence, not disposable build products.
Eighty-three historical blob versions exceed 1 MiB; all belong to three JSON
records: implementation bindings, API coverage inventory and release
obligations. The largest is the 6,644,657-byte API inventory. They were retained.

All 106 unavailable historical evidence inputs retain their original rows and
hash metadata. No evidence was reconstructed, removed or relabeled by this
audit. No Git history was rewritten.

## Publication review still needed

Native fixture provenance describes owned scratch projects; translation
profiles are explicitly unqualified. Raw receipts preserve original machine
paths and identifiers. Their provenance is useful evidence, but does not
independently establish customer-data clearance for publication.

The retained vendor manual image and vendor-derived test examples need an
explicit redistribution review before a public release. The repository's own
Apache license does not establish permission for those third-party inputs.
Keep the private repository and original evidence intact while that review is
open. A future publication packet should review/redact machine-specific data
without overwriting its original evidence.

## Reproduction and retained evidence

`artifacts/repository-audit-01/` contains `tool.json`, `scan-secrets.py`,
`history-secrets.log`, `history-secrets-redacted.json`, `inventory.py`,
`inspect-content.py`, `inventory.json`, and `triage.py`. The tracked JSON report
retains the per-finding decisions even when these local artifacts are absent.

The scanner command uses these options with the verified executable:

```text
gitleaks git --log-opts="--all --full-history -m --root" --config default.toml
  --gitleaks-ignore-path empty-ignore --ignore-gitleaks-allow --redact=100
  --no-banner --no-color --max-decode-depth 5 --max-archive-depth 3
  --report-format json --report-path history-secrets-redacted.json .
```

`default.toml` contains `[extend]` and `useDefault = true`; `empty-ignore` is an
empty directory. Gitleaks configuration environment overrides were removed from
the child process. See the [official Gitleaks documentation](https://github.com/gitleaks/gitleaks).

No application code changed and runtime tests were not repeated. The full
44-workstream goal, native qualification and publication review remain open.
