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

```powershell
python -B scripts/curate_evidence.py --id local.smoke.20261024 `
  --report artifacts/q24/smoke-scratch.json `
  --binary src/TiaMcp.Host/bin/Debug/net10.0-windows `
  --binary src/TiaMcp.Openness.V21/bin/Debug/net48 `
  --scope "What passed, on which fixture" --limitation "What it does not cover"
```

Then add the record to `contracts/feature-catalog.v1.json` with
`"artifacts": ["evidence/<evidence-id>.json"]` and cite it from the features it
qualifies. A summary with `source.dirty: true` ran uncommitted `src/` or
`scripts/` changes; commit first.
