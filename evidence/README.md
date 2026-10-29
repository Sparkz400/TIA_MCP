# Curated qualification evidence

A catalog feature may claim `verified` only when every evidence record it cites
has a passed summary in this folder (`scripts/test_contracts.py`, `claim_errors`).

Each `<evidence-id>.json` is written by `scripts/curate_evidence.py` from the
local raw reports of one qualification. It keeps the exact source commit, a hash
of every binary tree that ran, each raw report's SHA-256 and size, whether the
run completed, its step names, and the MCP tools that answered with and without
error. A verified feature's bound tools must all appear as answered without error.
Each cited summary must come from a clean worktree (`source.dirty: false`) at a
commit that is an ancestor of `HEAD`, so merge evidence PRs, never squash them.
A feature with a native acceptance gate needs every bound tool answered in a
record that is not Host-only (`.hostonly.`). The three catalog records that
predate curation are named in `LEGACY_UNCURATED`; every newer record needs a summary.

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
`artifacts/`, never into an outside folder. TIA refuses scratch and checkpoint paths over
143 characters, so keep the folders short:

- Name run folders with a one-letter lane and three letters, as in `artifacts/q/lun`: the
  library harness allows at most 45 characters for `--artifacts`.
- Seeders that keep their runs below `<exe>\..\..\..\..\..\..\artifacts` (UnitSeeder,
  ResolutionSeeder) need the frozen build itself inside the project at a short path, for
  example `git archive` into `artifacts/f`; that also keeps the frozen build in the project.
- A runner that needs a base of about 30 characters (`qualify_interruption.py --base`)
  can use a drive letter: `subst Q: <main checkout>\artifacts\q`, then `--base Q:\i3`.
- Junctions do not work: the HMI and library seeders refuse reparse points, and Python's
  `Path.resolve()` turns a junction back into the long path.

Curate from a checkout whose `HEAD` is the commit the frozen binaries were built from, so
the summary names that commit.

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
