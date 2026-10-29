# Curated qualification evidence

A catalog feature may claim `verified` only when every evidence record it cites
has a passed summary in this folder (`scripts/test_contracts.py`, `claim_errors`).

Each `<evidence-id>.json` is written by `scripts/curate_evidence.py` from the
local raw reports of one qualification. It keeps the exact source commit, a hash
of every binary tree that ran, each raw report's SHA-256 and size, whether the
run completed, its step names, and the MCP tools that answered with and without
error. A verified feature's bound tools must all appear as answered without error.

Raw reports, native exports, project files and logs stay in the ignored
`artifacts/` tree. They may contain local paths, process IDs or project content;
the summary's hashes tie it to them without publishing them.

Curation also copies each raw report into the main checkout's
`artifacts/evidence/<evidence-id>/<sha256>-<report>`, whichever worktree it runs
in: removing a worktree deletes that worktree's `artifacts/`. Keep that store when
cleaning `artifacts/`. `scripts/audit_evidence_store.py` (part of local
`Test-Offline.ps1` runs) fails when a summarized report is missing from the store
or changed. `raw-reports-lost.json` lists the reports that were already gone when
the store was introduced on 28 October 2026; their summaries and hashes stand, but
the runs can no longer be re-audited. A re-run of such a record is curated as
`<original stem>.rerun.<date>` and replaces it in the features that cite it
([re-runs](../docs/verification/evidence-reruns-20261028.md)). A few catalog records
from before curation cite raw files under `artifacts/` directly; the audit requires
them in place or listed in `uncuratedArtifacts` of the same file.

Every catalog record is cited by a feature. A record no feature cites any more, for
example one a re-run replaced, moves verbatim to
`docs/history/catalog-evidence-retired-<date>.json`; its summary stays here and its
reports stay in the store. `scripts/test_contracts.py` fails on an uncited record or
on a summary that is neither in the catalog nor in a retired list.

Native runs write their reports, snapshots and logs under the main checkout's
`artifacts/`, never into an outside folder; only the frozen `git archive` build may sit
outside the project (for example `D:\wp\bNN`). TIA refuses scratch and checkpoint paths
over 143 characters, which a deep `artifacts/` path can exceed. Then give the runner a
short directory junction that resolves into `artifacts/` (for example
`New-Item -ItemType Junction D:\wp\rr -Target <main checkout>\artifacts\rr`), so the path
TIA sees is short and the files still live in the project. Curate from a checkout whose
`HEAD` is the commit the frozen binaries were built from, so the summary names that commit.

```powershell
python -B scripts/curate_evidence.py --id local.smoke.20261024 `
  --report artifacts/q24/smoke-scratch.json `
  --binary src/TiaMcp.Host/bin/Debug/net10.0-windows `
  --binary src/TiaMcp.Openness.V21/bin/Debug/net48 `
  --scope "What passed, on which fixture" --limitation "What it does not cover"
```

Then add the record to `contracts/feature-catalog.v1.json` with
`"artifacts": ["evidence/<evidence-id>.json"]` and cite it from the features it
qualifies. The summary records the commit at curation time, so commit before
the run and curate before changing `src/` or the runner again. A summary with
`source.dirty: true` ran uncommitted `src/` or `scripts/` changes.
