# Bounded offline metadata index

`project_index` captures exposed identities once, then serves stable searchable pages without repeatedly traversing Siemens objects. The public tool takes a `request` object. The worker holds plain metadata only, never retained native object proxies.

```json
{"request":{"action":"capture","maxVisited":50000,"lifetimeSeconds":300}}
```

Copy the returned `indexId` into queries. Optional capture `projectName` and `plcPath` narrow the project/PLC. Query supports case-insensitive substring `text` on name/path, exact `kind`, `plcPath` and `scopePath`, and `maxItems` from 1 to 200. Kinds are hardware, plc, softwareUnit, safetyUnit, block, type, tagTable, tag, constant, systemConstant and technologyObject.

```json
{"request":{"action":"query","indexId":"<returned id>","kind":"block","text":"Motor","maxItems":100}}
```

Repeat the same filters and add the returned `nextCursor` until it is null. Cursors are authenticated, bound to snapshot, filters and worker lifetime. Rows sort by canonical path; duplicate identities fail capture. Do not treat an absent match as proof of absence when `completeness.scopeComplete` is false.

`status` returns metadata, `release` frees a snapshot and `verify` recaptures its original scope. `query` with `verifyCurrent:true` also recaptures and rejects a changed index. An observed difference invalidates the snapshot. Known local mutating/session operations invalidate before execution, including failures. New worker operations default to invalidating until classified as read-only.

Cached responses say `freshness:snapshotOnly`: UI edits and other clients do not automatically invalidate a cache. Verification is point-in-time metadata verification. The SHA256 `metadataRevision` covers project copy path/name, exact indexed rows, completeness and warnings. It is **not a PLC logic hash or whole-project revision**, and cannot prove unchanged block bodies, libraries, detailed hardware settings or TO parameters. Native exclusive access bounds each capture; intervening edits remain possible after it ends. `completeness.complete` is always false for the whole project.

Limits: 100–50,000 visited native items (including groups), traversal depth 64 from shared native iterators, 50,000 rows, 32 MiB accounted UTF-8 payload per snapshot, two retained snapshots, 16 cached queries each, 30–900 seconds lifetime. Each row is at most 64 KiB and each page at most 512 KiB of row JSON plus a bounded metadata envelope. Exceeding memory capacity fails; traversal limits return explicit partial coverage. Capture is synchronous on the worker STA; transport timeout remains terminal and no operation is replayed. Use a smaller PLC scope/budget when needed.

Pure collection benchmarks are documented in `project-index-store-tests.md`. They do not measure native TIA traversal or VM/network latency. Native evidence is recorded separately by `qualify_project_index.py` against an explicitly created scratch project.
