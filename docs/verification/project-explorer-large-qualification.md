# Large native tag fixture qualification

The final bundle `tia-mcp-v21_20261008_125230_8b2f8ce9` passed the large native explorer flow recorded in `artifacts/project-explorer-native-large-tags.json`. Its portable artifacts are `artifacts/project-explorer-native-large-tags.snapshot.json` and `artifacts/project-explorer-native-large-tags.html`.

This is a genuine native fixture with one standard CPU, its existing SCL blocks and hardware ancestry, and **2,000 distinct Byte memory tags** named `ExplorerScale_0000` through `ExplorerScale_1999`, at `%MB0` through `%MB1999`. It qualifies this metadata shape and size; it does not claim 2,000 functions, large HMI content, physical topology, or runtime behavior.

The runner created a separate owned offline scratch. Its single mutation used the existing prepared `tag_table_import_xml` route with checkpoint, compilation and save. The native import applied successfully in 28.41 seconds; preparation plus apply/compile/save took 30.05 seconds. No production limits or compiler were changed. The full 1,754,761-byte native table export was transferred with SHA-256 verification and parsed independently to check every name, type, canonical path and address. A bounded inspection row list was not treated as complete.

The final read-only verification completed 85 recorded tool calls and 17 checks:

- All 2,070 captured nodes had correct ancestry and native/virtual identity distinctions.
- Every search page and all child partitions matched independently expected contents without duplication or omission.
- All 2,000 filtered declarations matched the native XML oracle, and legacy `project_index` callers remained valid.
- Exact tag 1999 details and native block content were available. The existing authored program retained its 15-edge call/instance/member relationship evidence.
- Exact-reference verification explicitly excluded content freshness. Stale session selection, released snapshots, mutation attempts through the explorer route, and session loss were rejected.
- JSON and HTML exports completed, and the native snapshot passed the versioned schema.

| Measurement | Observed result |
| --- | ---: |
| Native capture | 10,237.06 ms |
| Captured nodes / native visits | 2,070 / 2,070 |
| Native capture truncated | false |
| Captured snapshot bytes | 1,679,317 |
| Cached MCP search median, 25 calls | 40.44 ms |
| Cached MCP search p95 | 79.59 ms |
| Cached MCP search maximum | 86.01 ms |
| Maximum returned page payload | 417,490 bytes, below 512 KiB |
| Separate native relationship analysis | 5,877.21 ms |
| Final JSON export with observations | 1,729,446 bytes |

These separate native traversal, fixture creation, cached MCP transport/query, and export costs. They are measured results for this machine and fixture, not universal project-size latency guarantees or browser rendering measurements.

The original fixture report is preserved as `artifacts/project-explorer-native-large-tags-fixture.json`. It recorded the correct native rejection code `ModelInvalidated` after the prepared import; an initially incomplete verifier code allowlist caused that run's assertion to stop. The corrected verifier continued strictly read-only against the exact saved project, carried the original invalidation evidence and import timing, and never repeated the successful import.

The runnable qualifier is `scripts/qualify_project_explorer_tags.py`. It is pinned to the final bundle, and its `--verify-report` mode permits only read-only continuation of the known owned fixture. The six tests in `scripts/test_project_explorer_tags.py` passed, including missing/duplicate declaration and wrong-address/type rejection. Unknown native outcomes are recorded without automatic replay.
