# Portable CI and journal repairs — 16 October 2026

This workstation has no Siemens products, SDKs or PLC simulator. None of the
checks below establishes native engineering, runtime or release acceptance.
The [full execution queue](../history/offline-execution-20261016.md) remains open.

## Changes

- `Test-Offline.ps1` selects one Git application in PATH order. PowerShell returns
  multiple application paths when multiple installations are present; passing the
  array to the real Git suite broke its four-argument contract. Three regression
  checks exercise the actual runner assignment with one, multiple and zero Git
  executables, including a fixture path containing spaces.
- All three native-worker journal replacement call sites use the shared
  `JournalFileReplacement`. Windows `File.Replace` can fail with HRESULT
  `0x80070497` during concurrent reads even when readers allow delete sharing.
  Only that error is retried: at most ten replacement calls and nine 10 ms waits.
  Other errors propagate immediately. The reader continues to deny in-place writes
  so its returned bytes and full-file hash come from one immutable open file.
- The linked-source PLC integration suite and native ExpansionEvidence harness
  include the helper, as do both worker projects. Native build verification remains
  unavailable here; their source/project changes are not a claimed worker build.
- Deployment qualification report writes now use the existing bounded
  `replace_owned_pending` helper. Retry is limited to the same pending report;
  it never repeats the MCP request. Permanent errors retain pending bytes and fail.
- Python onboarding installs the actual dependencies. The new read-only retained
  evidence audit inventories each expected digest and dependent evidence/feature
  ID, and can strictly verify a recovered directory without importing its contents.

Microsoft documents that error 1175 leaves both filenames unchanged. Errors
1176/1177 can leave different replacement states and are deliberately excluded.
See [ReplaceFileW return values](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew).
An ignored diagnostic probe reproduced error 1175 with both read/delete and
read/write/delete sharing; granting write sharing did not resolve the race.

## Executed evidence

| Check | Observed result |
| --- | --- |
| Hosted CI at `8b35c1c880c01d992662b8f0d3d4278ab47c9203` | Python, managed and OPC UA and Explorer pass. These runs contain the Git selection fix, not the later journal/report repairs. |
| Core targeted suite | 145/145 pass, including concurrent artifact downloads against the real library journal writer, finite retry exhaustion, partial-replacement/error refusal and interrupted-delay propagation. |
| Standalone PLC library integration | 192 pass, zero fail after correcting its linked helper dependency. |
| .NET Framework compatibility probe | Shared helper compiled against installed .NET Framework 4.6.2 reference assemblies and replaced a real file on runtime `4.0.30319.42000`. Probe retained at `artifacts/journal-probe/FrameworkProbe.cs`; this is not a Siemens worker build. |
| Deployment qualification | 17/17 protocol-fake checks pass, including transient report replacement without request replay and nonretryable failure before process startup. |
| Retained evidence audit | Eight focused checks pass, including an actual Windows junction. Current inventory: six exact byte matches and 106 missing files. |
| Python partition, snapshot `offline-03` | 1,248 tests, one skip: unavailable task-local native observation in `test_block_test_project_open`. Contracts/bindings and integrated evidence inventory pass. This precedes the deployment report repair. |
| Browser baseline | 24 checks pass with zero network/page errors, including synthetic 1k/10k/50k trees. `output/playwright/project-explorer/2026-10-16T10-42-55-684Z-18908`. |
| Isolated OPC UA | Passed; `artifacts/opcua-offline/0d4ade2d12e64cb4b38bf2d7861a962b/result.json`. |
| Combined portable source | Passed from immutable `artifacts/offline-05/source`: all 27 managed suites, 1,250 Python tests with one native-evidence skip, 21 Explorer model/client checks, contracts/bindings/evidence inventory and actual translation, translation admission, Test Suite, catalog, watch-boundary and OPC UA stdio checks. `source/artifacts/checks/result.json`; source manifest SHA-256 `994f2bd0032c39fd2dc62e133be0aff82ffb7b0c83f1692a86ef924154ea3b34`. |
| Host-only README build | Passed using the documented command; zero warnings/errors and the stated executable exists. `artifacts/host-only-build.log`. |

Snapshot `offline-06` contains the final evidence-audit CLI refusal for a retained
file replaced by a directory, its regression check and documentation updates.
Its `src/`, `contracts/` and managed `tests/` files are byte-identical to the
passing combined snapshot. Its separate Python partition passes all 1,250 tests
with the same single unavailable-native-observation skip;
source manifest SHA-256 `669bb53df501b5a6a375dfc594379434de82db1d9dabd669de83243309e5a1e5`.

GitHub has no open issues or pull requests at this checkpoint. The only local
branch/worktree is the primary `main`; no task checkout requires removal.

## Retained failed attempts

- `offline-01`: Python/contracts/bindings, 21 Explorer model/client checks and
  15 managed suites passed; Core finished 143/144 because of the Windows journal
  replacement race. Later suites did not run.
- `offline-02`: fixed Core 145/145 and 19 complete managed suites passed; the next
  suite failed compilation because its linked library writer lacked the new helper.
  The harness dependency correction followed this failure.
- `offline-04`: Python stopped on Windows access-denied replacing the deployment
  report. Its contract/binding/evidence checks passed, but managed suites did not
  run. The deployment script subsequently adopted the existing report retry helper.

Each attempt retains its own source manifest, runner output and `result.json`.
No failed result was overwritten, omitted from the accounting or treated as a pass.
Historical native evidence digests remain unchanged in the refreshed bindings;
106 missing artifacts remain missing. Native compilation, real target identities,
preservation/reopen/recovery, PLCSIM/physical behavior and production admission
must be checked on the required Siemens environment with the eventual exact build.
