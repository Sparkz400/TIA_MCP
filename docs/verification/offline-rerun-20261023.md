# Fresh offline regression and portable package after the stop — 23 October 2026

This closes the resume item from the [user stop checkpoint](user-stop-20261023.md).
Both interrupted batches were reconciled and then rerun fresh against one frozen
source. The full `All` aggregate and the portable package both pass. No TIA Portal,
PLCSIM, native worker or Openness registration was started or written; the native
work running at the same time on this machine was not touched.

## Source

- Commit `9d38c26bdf8b42d7229fab62e8f6e832628568a1` on branch
  `claude/offline-rerun-20261023-7253ab`, clean worktree.
- Isolated snapshot: `artifacts/deployment-build/fa44ddb770ec427d9e4ccfcb68411ff6`
  (2,904 captured inputs; `build-inputs.json` SHA-256
  `97459c80fa98ca8fced5ae694c8ac8e3b0e8c0abb0649b058ccf2478a2aea852`,
  `build-outputs.json` SHA-256
  `99467589cd7cf86ecfc3c55b23880a269a2e7544b8a374ed22aa5539a7bcb906`).
- `artifacts/offline-rerun-20261023/snapshot-comparison.json`: every captured input
  hash equals the committed file; 0 mismatches, 0 missing, 0 untracked. Only
  `.editorconfig`, `.gitattributes` and `.gitignore` are outside the snapshot's
  capture set by design.
- Bundle: `artifacts/releases/tia-mcp-offline_20261023_162400_000d73af`
  (2,153 files), manifest SHA-256
  `4f9e37505b7fd936889c8ab296d830e5f38beadfb657fd546d8d444d653eda74`; adjacent ZIP
  SHA-256 `d5e2abe831f46c931c03f99cfb778955be319bf4c47d7710600f34ae895009cd`.
  The offline Host build reported 0 warnings and 0 errors.

All paths are relative to this worktree's checkout. `artifacts/` is ignored by Git,
so the evidence exists only in that checkout, not in the main checkout.

## Commands

The PowerShell 7 drivers are kept next to the evidence. They prepend the project
`.venv` (`jsonschema` 4.26.0, `psutil`, `PyYAML`, `referencing`), because
`python` on this machine's `PATH` resolves to an unrelated environment.
`run-environment.json` records the interpreter, the `pip freeze` output,
.NET SDK 10.0.402, Node v24.20.0 and Git.

1. `stage1-package.ps1`: `scripts/Package.ps1 -Offline`, which runs
   `New-IsolatedBuild.ps1 -Offline`, publishes the Host, strictly verifies the
   bundle and writes the ZIP. Log: `package-build.log`.
2. `stage2-offline.ps1`: `<snapshot>/scripts/Test-Offline.ps1 -Suite All
   -OutputDirectory <snapshot>/artifacts/<run>`, run from inside the frozen snapshot.
3. `stage2-package.ps1`: `scripts/Test-OfflinePackage.ps1 -BundlePath <bundle>
   -OutputDirectory artifacts/offline-rerun-20261023/package-01`.

Steps 2 and 3 ran concurrently, as the stopped batches did.

## Results

| Run | Evidence | Outcome |
| --- | --- | --- |
| Package build | `stage1-result.json` | passed, 0 warnings |
| Aggregate attempt 01 | `<snapshot>/artifacts/offline-rerun-01/result.json`, `offline-run.log` | **failed** (see below) |
| Aggregate attempt 02 | `<snapshot>/artifacts/rerun-02/result.json`, `offline-run-02.log` | **passed** |
| Portable package | `package-01/result.json`, `package-run.log` | **passed** |

Aggregate attempt 02 ran feature-contract coverage (261 tools, 56 mutation kinds,
335 features), 9 project-model contract tests, 1,930 Python tests (1 skipped), all
45 managed suites (124/124 VCI checks), 25 Explorer model/client checks, 6
relationship-scope checks, and every configured Host workflow. The final UA
diagnostic binding workflow passed 946 checks. `nativeQualification` is `false`.

The package passed all 53 stages: deployment guards; strict original and
relocated (`relocated Über bundle`) verification with identical manifest hashes;
packaged MCP and exact-ZIP smoke tests; and every packaged workflow through the
prebound UA diagnostic binding (938 checks). Final strict verification of the
relocated bundle passed. `nativeExecuted` is `false`.

Result hashes (SHA-256):

- attempt 01 `result.json`: `5ccc46fd3f5cbd0ebc7b0fc56a8bfd7d059cc518c3db0b839df843e2d75c65d7`
- attempt 02 `result.json`: `a21847deacb0a729319d080e31444f05bff62b68cba9874c63a9321eb113e7c9`
- package `result.json`: `73167344db09ce898b3075a6da3604f2d2b01777b5880f881631a42609831e82`
- `offline-run.log`: `04286a0cd823f780f1385e25afd7013d0a818dd40f715bc5c198053749c8c25e`
- `offline-run-02.log`: `c8a4b55c94409089c54c937f4957dff54ab5e9febda469732f3d44aacaad5f05`
- `package-run.log`: `5547b903e23857eb9cc217f31dd392cc2720e610201d188a261eaba9507a55d6`

### Retained failure: aggregate attempt 01

Attempt 01 passed the Python, contract, Explorer and first five managed suites,
then stopped in `TiaMcp.VersionControl.Tests` with 123/124 checks. The failed check:
`Git long-path workspace retains raw commits across restart and export:
Select a fixture root of at most 165 characters.`

Root cause: `Test-Offline.ps1` requires its output to be inside the snapshot,
and this snapshot sits in a worktree (`.claude\worktrees\offline-rerun-20261023-7253ab`).
That path made `<output>\git\long-path` 171 characters. `GitLongPathChecks`
deliberately refuses roots over 165 characters, so the Git process's working
directory stays below Windows MAX_PATH while its data paths exceed it. The guard
behaved as designed, and no source changed. Attempt 02 used the shorter
output name `rerun-02` (fixture root 163 characters) against the same snapshot
and build inputs. Attempt 01's result and log are retained unchanged.

## Reconciliation of the stopped batches

The checkpoint names these logs and partial artifacts:

- `artifacts/todo-scale-integration-20261023/offline-run.log` and `package-run.log`
- `artifacts/deployment-build/50bec2d5a85541e382fa81d254434a6d/artifacts/offline-scale-01/`
- `artifacts/todo-scale-integration-20261023/package-01/`
- `artifacts/user-stop-20261023-checkpoint/offline-cancellation-*.json`
- `artifacts/releases/tia-mcp-offline_20261023_110450_156cbbdc`

None of them exists on this workstation (`PCNICO`). They are absent from the main
checkout `D:\Entwicklung\TIA_MCP\artifacts`, a recursive search of `D:\` and the
user profile, and the other local roots. The main checkout's `artifacts/releases`
holds only the 16 October bundle. None of the earlier 23 October integration
evidence is present either. The 23 October batches therefore ran in another
checkout. Their partial progress could not be inspected here, and nothing was
modified or deleted.

From the checkpoint record and the scripts, the stopped batches were the same two
entry points used here: `Test-Offline.ps1 -Suite All` inside an isolated offline
snapshot, and `Test-OfflinePackage.ps1` on the bundle from that build. Before the
stop, only the isolated offline Host build and publication had finished (0
warnings, 2,150-file bundle, manifest `50129d5d…6720`). Neither batch published a
final result, and both remain interrupted (exit `-1`), not passes. This document
supersedes them only through the fresh runs above, under a new source snapshot,
bundle and evidence directory.

## Limits

- Offline only: no Siemens/native, PLCSIM, runtime or device qualification is
  claimed, and the bundle excludes native workers. No Openness AllowList entry
  references this worktree, checked read-only.
- The retained-evidence audit inside the snapshot lists 112 retained inputs as
  missing, because isolated snapshots carry no `artifacts/`. Likewise, one Python
  test skips because it needs a task-local native observation. Both are the
  scripts' declared offline behaviour, not passes of those inputs.
- On its first run with the worktree-local `DOTNET_CLI_HOME`, the .NET first-run
  experience installed an untrusted ASP.NET Core HTTPS development certificate
  in the current user's store.
- No hosted CI run, release publication or installation is claimed.
