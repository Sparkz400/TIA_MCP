# Scoped tag-table import: native qualification (7 November 2026)

Evidence: `local.tag-table.scoped-import.20261107`
([summary](../../evidence/local.tag-table.scoped-import.20261107.json)), issue
[#209](https://github.com/Sparkz400/TIA_MCP/issues/209). It covers the
`tag-table-content-and-project-metadata-v1` policy described in
[tag-table import scope](../guides/engineering-workflows.md#tag-table-import-scope).

## Run

`scripts/qualify_tag_table_scope.py` ran on Debug builds of `e5445ef6`
(TIA Portal V21 Update 3, PCNico). It used one scratch project with a CPU 1511-1 PN,
created and owned by the run. 17 of 17 steps passed. The summary's source commit
`8c5f32f7` adds only a curator change on top of `e5445ef6`; `src` and every
executable that ran are the same.

| Step | Result |
| --- | --- |
| Fixture seeds tables `Signals` (`Existing` `%M50.0`) and `Other` (`Untouched` `%M51.0`) and an inconsistent SCL FC `TagScopeBroken` | Block reported `isConsistent: false` |
| Candidate without `Existing` | `TagScopeMismatch` at prepare |
| Add `Added` (Bool `%M50.1`) | Prepares despite the inconsistent block; the plan's scope policy is the scoped one |
| Apply with checkpoint, compile, save | `applied`. One baseline error (`Tag "UnresolvedFlag" not defined`), the same error after the change |
| Native readback | `Added` and `Existing` present |
| `change_restore` | `ScopedRecoveryRequired` |
| External edit of the unrelated table `Other` after prepare | `StaleRevision` at apply |
| Native consumer proof | `TagScopeConsumer` (FC3) reading `Added` compiles as Bool; with `Added` as Int at `%MW60` TIA reports `Data type 'Int' cannot be converted implicitly into data type 'Bool'`; the tag is then restored |
| Same type change through MCP | `CompileFailed`, `recovery_required`, `before.zap21` kept; diagnostics are the baseline error plus the consumer error |
| Shutdown | Worker exited on its own with code 0; project lock release not verified |

## Negative case

The first negative case imported an unused tag at the out-of-range address
`%M20000.0`. V21 Update 3 accepted it without a compiler error, so the case could
not fail. A tag only produces a compile error when a block uses it. The fixture now
generates a consistent block that reads the tag, and proves the native diagnostic
before the qualifier applies the same change through MCP.

## Not covered

Software units, namespaces, user constants, multilingual comments, F-PLCs,
reopened projects and interrupted outcomes. Neither was a packaged or installed
build run, nor was project-lock release verified.
