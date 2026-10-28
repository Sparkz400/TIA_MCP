# Native library scale — 26 to 28 October 2026

A project library in an owned TIA Portal V21 scratch was grown 100 types per guarded
`library_create_type` change, to 5,000 types and 10,004 traversal entries. Every change ran the
normal product path: revision and fingerprint walks, whole-project checkpoint archive,
`CreateFromDocuments`, compile of all devices, save. These are observed timings on one Windows
host, not cross-machine guarantees.

Runner: `scripts/qualify_library_scale.py` (offline checks: `scripts/test_library_scale.py`).
Evidence: `local.library.native-traversal.20261027` (complete traversal of the run-6 library) and
`local.library.native-chain.20261028` (the last releases and the complete 1,000-node closure of the
chain). The growth runs stopped before their runner completed (CPU limit, or stopped during an
over-budget final probe), and the chain was built and released over several runs, so those reports
are not evidence records; their raw reports stay under `artifacts/ls/`, pinned here:

| Run | Report | SHA-256 | Worker SHA-256 (prefix) | Runner commit |
| --- | --- | --- | --- | --- |
| 6 | `scl864f5c0f/qualification.json` | `aee2fc111d3c82b0f201ae6c318138fce90eedb8b06ed2e431162f6113164428` | `a823800ab8a1` | `fbdf91d` |
| 3 | `scl4be9621a/qualification.json` | `2ae7098274a9e16f42c26de66c19896bea37d0f48b572794100d4d3342f5f5ed` | `a823800ab8a1` | `8e33624` |
| 2 | `scl9ac7710d/qualification.json` | `f146cf55366708f3569d6541f3d49f755c2548f21d61e7e4ba1cb9954528f9f5` | `c1180ab130d8` | `65fd13b` |
| Chain: create | `scl08284524/qualification.json` | `299b2624e9644394f9f3e54d4435275c56f789af32ff8f294fbe59617ec220e0` | `c738ab79417b` | `db8cee3` |
| Chain: release 0–3 | `scl900d2a96/qualification.json` | `df8fd145d2a9c30174ebded12741049ff7b60a8a4d58c62ce49ff2b082b35868` | `c738ab79417b` | `ec23812` |
| Chain: release 4–303 | `scl7c211165/qualification.json` | `2cb3b216a3f3620132b7c43a9465a714bb795363d47b2e34f12969f8c707a48a` | `13a8cf16d3d6` | `99e4485` |
| Chain: release 304–553 | `scl8d21c0be/qualification.json` | `19dd85243d4822456818716981ab8dc4cffaa2177306350e014dcd6ef6e95c41` | `13a8cf16d3d6` | `8ccca8f` |
| Chain: release 554–883 | `sclb2b7ec1d/qualification.json` | `3acb41fa5407601bd5bcd8ddd3fd9983e49ea7a253d77faa368aa0cad2614678` | `13a8cf16d3d6` | `8ccca8f` |
| Chain: release 884–999 | `scl2e0c7699/qualification.json` | `17feea910f61bbb6f3caea9d64929c38404525af28a0d39bb7dbf7c59585598d` | `13a8cf16d3d6` | `8ccca8f` |

## Fixes the runs forced

- **Openness instance cap.** TIA allows 500,000 live Openness object instances per Portal.
  Proxies are released only when finalized, so long library walks accumulated until
  `EngineeringOutOfMemoryException` at about 1,000 types. The worker now collects and finalizes
  after every request (`Program.cs`). Apply at 1,000 types dropped from 387 s to 90 s.
- **One library walk per query page.** `library_query` (`elements`) hashed the revision, walked
  again for its items, and rehashed afterwards: three full metadata walks per page. Its rows now
  come from the revision snapshot. Above the 10,000-entry item budget they are the snapshot's sorted
  first 10,000 rows, still flagged truncated. At 3,000 types one page took 211 s before and 34 s
  after; at 10,004 entries 230 s before and 56 s after. A page now costs the same as `library_list`.
- **Bulk type creation.** `library_create_type` accepts `documentNames` (1..100 distinct names,
  the library selection limit) and creates one type per document in one change.
- **Batch release.** `library_release_version` accepts 1..100 InWork versions and releases them in
  selection order (callee before caller) in one change. One release per change cost about 150 s at
  1,000 types.

## Native limits observed

- **CPU object limit.** The scratch CPU 1511-1 PN (V4.1) allows 4,000 blocks and PLC data types
  together. Runs 2 (3,900 FBs) and 3 (1,000 FBs + 2,900 UDTs) failed the next compile with "The
  maximum number of 4000 blocks permitted for this CPU was exceeded"; the change ended
  `recovery_required` with its checkpoint retained, as designed. Run 6 adds a second PLC of the
  same CPU after 2,500 types.
- **Native archive refusal.** One attempt (run 4) failed in `Project.Archive` during the
  checkpoint with "FS Server error: The reorganization on the storage … PEData.plf is rejected".
  The journal shows the failure before any native mutation; the change ended
  `recovery_required`. The identical rerun passed that point.

## Run 6: 1,000 FB + 4,000 UDT types on two PLCs

| Types after batch | Prepare | Apply | Checkpoint archive | Portal RSS | Worker RSS |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 1.0 s | 13.7 s | 0.34 MB | 3.85 GB | 64 MB |
| 1,100 | 31.1 s | 85.8 s | 2.93 MB | 3.46 GB | 152 MB |
| 2,100 | 51.5 s | 127.6 s | 4.32 MB | 4.06 GB | 176 MB |
| 3,100 | 87.5 s | 214.7 s | 5.82 MB | 3.70 GB | 237 MB |
| 4,100 | 93.5 s | 219.4 s | 7.21 MB | 3.91 GB | 228 MB |
| 5,000 | 152.9 s | 369.8 s | 8.45 MB | 3.73 GB | 256 MB |

Batch 25 (2,600 types) is the first on the added PLC. Portal memory stays between 3.0 and 4.3 GB
across all runs; worker memory stays below 260 MB.

| Library | Entries | `library_list` | One `library_query` page |
| ---: | ---: | ---: | ---: |
| 1,000 types | 2,002 | 10.0 s | 9.5 s |
| 2,000 types | 4,002 | 19.0 s | 18.9 s |
| 3,000 types | 6,002 | 33.7 s | 34.3 s |
| 4,000 types | 8,002 | 38.7 s | 38.8 s |
| 5,000 types | 10,004 | 71.5 s | 230.2 s (three walks, before the over-budget fix) |
| 5,000 types, reopened | 10,004 | 54.0 s | 56 s (one walk) |

The complete traversal reopened the saved run-6 project in a fresh Portal and paged `library_query`
under one revision token: 50 pages of 200 items, 10,000 items, 2,805.8 s. The result reports
`truncated: true` and `scopeComplete: false`: 2 of the 10,002 rows lie beyond the item budget.

## 1,000-node dependency chain

A second scratch project library holds 1,000 FB types `Chain_0000`..`Chain_0999`, each calling its
predecessor. It was created in 10 guarded 100-type changes (prepare 1.0 → 34.2 s, apply 23.5 →
394.8 s, checkpoint 0.36 → 3.30 MB, Portal 3.4–4.0 GB, worker ≤ 110 MB). The final probe paged its
2,002 entries completely in 11 pages, 110.6 s.

TIA records library dependencies only between released versions: with every version InWork, the
dependency query from the chain head returned one node. The chain was therefore released bottom-up
over five reopened runs (the stop file paused it between TIA slots):

| Versions | Versions per change | Seconds per change |
| --- | ---: | ---: |
| 0–3 | 1 | 149–159 |
| 4–303 | 100 | 497–771 |
| 304–553 | 25 | 333–681 |
| 554–883 | 15 | 389–687 |
| 884–999 | 10 (6 in the last) | 355–519 |

Each change carries the fixed cost of checkpoint, compile and save; the cost per released version
grows with chain depth. 100 versions near the bottom cost 497 s in one change, while 10 versions near
the top cost about 490 s, so batches were shrunk to stay well under the Host's 900 s per-call limit.

`library_query` `dependencies` from the last released version returned the whole closure every time,
complete and untruncated, in about the same time regardless of size:

| Root | Nodes captured | Seconds |
| --- | ---: | ---: |
| `Chain_0003` | 4 | 22.9 |
| `Chain_0553` | 554 | 28.7 |
| `Chain_0883` | 884 | 23.0 |
| `Chain_0999` | 1,000 | 22.8 |

## Run 2: 3,900 FB types, before the query fix

| Types after batch | Prepare | Apply | Checkpoint archive |
| ---: | ---: | ---: | ---: |
| 1,100 | 40.4 s | 111.6 s | 2.94 MB |
| 2,100 | 81.1 s | 196.2 s | 5.47 MB |
| 3,100 | 224.3 s | 565.0 s | 8.02 MB |
| 3,600 | 268.7 s | 653.9 s | 9.29 MB |

FB-only batches cost up to twice as much per change as the UDT batches of run 6 at the same
library size: compile and archive scale with program size, not only with type count. One page
took 252 s at 3,500 types (three walks).

## Budgets

Measured on this host; a change exceeding them should be split or run from a smaller project.

| Quantity | Budget on this host | Basis |
| --- | --- | --- |
| Types per `library_create_type` change | 100 (enforced) | library selection limit |
| Versions per `library_release_version` change | 100 (enforced); ≤ 10 at chain depth above 850 to stay under 520 s | chain runs |
| Dependency closure query | ≈ 23–29 s up to 1,000 nodes | chain runs |
| Apply per 100-type change | ≤ 400 s up to 5,000 mixed types; FB-only programs up to 654 s at 3,600 | runs 6 and 2; Host per-call limit is 900 s |
| Prepare per 100-type change | ≤ 160 s up to 5,000 mixed types; up to 269 s FB-only | runs 6 and 2 |
| Checkpoint archive | ≈ 0.17 MB per 100 mixed types, ≈ 0.26 MB per 100 FB types; 8.45 MB at 5,000 | runs 6 and 2 |
| Memory | Portal 3.0–4.3 GB; worker ≤ 260 MB | runs 2, 3, 6 |
| `library_list` or one query page | ≈ 10 s per 1,000 types up to 4,000; ≤ 72 s at 10,004 entries | run 6 and traversal |
| Complete traversal of 10,000 entries | ≈ 47 min (50 pages) | traversal |
| Query item budget / revision budget | 10,000 / 50,000 metadata entries | worker limits |
| Types per S7-1500 CPU 1511 | 4,000 blocks and PLC data types together | native compile error |

Changes near the Host's 900 s per-call limit should be split or run against a smaller project; a
truncated query is reported, never silently shortened.

## Still open

- Real vendor byte volumes. The 100-component import and a generated 150 MB vendor-sized package
  passed separately ([import scale](library-import-scale-20261027.md)).
- Global libraries, branching dependency graphs and component apply at this scale.
