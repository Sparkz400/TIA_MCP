# Current status

TIA MCP is pre-1.0; use it only with offline projects, copies, scratch projects and
simulators you own (see the [README](../README.md)). On `main`, 29 October 2026:

| | |
| --- | --- |
| Feature catalog | `0.4.72`: 450 features, 371 verified, 79 unverified ([catalog](../contracts/feature-catalog.v1.json)) |
| Release matrix rows | 122 shipped, 246 limited, 64 unqualified, 8 unavailable on V21, 10 deferred |
| Matrix cells | 1,704 bound, **284 open**, 49 blocked, 30 deferred ([matrix](plans/release-matrix.v1.json)) |
| Release obligations | 0 of 44 accepted ([ledger](plans/release-obligations-20261015.md)); refreshed on 28 October against catalog `0.4.68`, D02, D04, D06, D12 and X04 on 29 October against `0.4.70`, and D09 against `0.4.72` |

Release scope: every feasible capability ships before 1.0; missing prerequisites and
unresolved feasibility stay release blockers ([release ledger](plans/release-obligations-20261015.md)).

## What is open

[TODO](TODO.md) lists the open work; [open matrix rows by owner](TODO.md#open-matrix-rows-by-owner)
names every row with an open cell.

- **External prerequisites:** real OPC UA and web servers of an owned PLCSIM instance in
  TCP/IP mode (runtime UA, web and capture rows), an operator at the desktop for the last
  Test Suite session (TS-Q01), the VMware guest,
  a V20 library input, RT Professional and other HMI targets, and a physical PLC.
- **Recorded V21 boundaries:** Startdrive drives expose no device compile service, the
  station upload meets TLS verification it never trusts, an OPC UA reference-namespace import crashes TIA,
  Unified text lists have no import route, and force-table rows can only be authored in the
  TIA editor.
- **Rows that cannot close on V21:** eight `unavailable_target` rows (49 cells:
  `change.call_connection_set`, `change.project_attributes_set`, `change.hmi_engineering`,
  the three block-protection kinds and the two OPC UA reference-namespace kinds) are blocked
  and do not count toward 1.0 (decision of 29 October).
- **Release mechanics:** the engineering package, version, tag and release notes.

## Where the evidence is

The [documentation index](README.md#evidence-and-status) explains the chain from catalog
claim to raw report. In short: each verified feature cites curated summaries in
[`evidence/`](../evidence/README.md); their raw reports are kept in the main checkout's
ignored `artifacts/evidence/` store and checked by `scripts/audit_evidence_store.py`.
84 raw reports of 74 evidence IDs were gone before that store existed
([list](../evidence/raw-reports-lost.json)). Overnight re-runs replaced 44 of them with 40 new
records ([re-runs](verification/evidence-reruns-20261028.md)), and the 29 October re-runs
replaced the rest that can run on this PC ([record](verification/native-reruns-20261029.md)).
No verified feature rests solely on a lost report; the [TODO](TODO.md#lost-evidence) lists
what is left. Every catalog evidence
record is cited by a feature; records no feature cites any more are in the retired lists under
[history](README.md#history).

## Recent changes

Dated notes, newest first, are in the [status log](history/status-log.md). On
[29 October](history/status-log.md#29-october-2026): the [MCP surface](reference/mcp-surface.md)
gained `--toolsets`, tool titles, workflow prompts, progress heartbeats and output schemas; the remaining re-runnable lost records
re-run and 21 replaced records retired (catalog 0.4.70); native qualification of web user
pages, OPC UA interfaces and role mappings, the Add-In companion, force-table edits and the
logged-in online compare; Test Suite TS-Q01 ran natively and its defects are fixed; earlier, 53
uncited records retired (0.4.69), dated plans moved to history and the TODO made the one open list. On
[28 October](history/status-log.md#28-october-2026): lost evidence re-run (21 host-only and 18
native records); `safety_import_xml` now compiles every device; catalog evidence retired and
the release ledger refreshed; STATUS, TODO and the docs index restructured; raw evidence store
and encoder test leak fix; Test Suite definitions and scope probe; runtime real-server Tasks
1–3; project translations under V21 Update 3; the composite rebind plan; I-device transfer
areas and GSD export; watch-table lifecycle and Host-loss reconciliation; graphical network
edits; connections, GRAPH, VCI mappings and CAx.

## Updating this page

When a change lands, add its dated note at the top of the
[status log](history/status-log.md) under its date heading, then update the numbers
and lists above. Keep this page short; detail belongs in the log, `verification/` and
the catalog.
