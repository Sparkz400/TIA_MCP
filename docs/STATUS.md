# Current status

TIA MCP is pre-1.0; use it only with offline projects, copies, scratch projects and
simulators you own (see the [README](../README.md)). On `main`, 29 October 2026:

| | |
| --- | --- |
| Feature catalog | `0.4.81`: 461 features, 421 verified, 40 unverified ([catalog](../contracts/feature-catalog.v1.json)) |
| Release matrix rows | 123 shipped, 298 limited, 25 unqualified, 5 unavailable on V21, 10 deferred |
| Matrix cells | 1,924 bound, **141 open**, 31 blocked, 30 deferred ([matrix](plans/release-matrix.v1.json)) |
| Release obligations | 0 of 44 accepted ([ledger](plans/release-obligations-20261015.md), [how acceptance works](plans/release-acceptance.md)); refreshed on 28 October against catalog `0.4.68`, D02, D04, D06 and X04 on 29 October against `0.4.70`, C03, D12 and D16 against `0.4.72`, and D09, F00, L01, L03, D02, X01, X04, X07, X08, X10 and X11 against `0.4.76` |

Release scope: every feasible capability ships before 1.0; missing prerequisites and
unresolved feasibility stay release blockers ([release ledger](plans/release-obligations-20261015.md)).

## What is open

[TODO](TODO.md) lists the open work; [open matrix rows by owner](TODO.md#open-matrix-rows-by-owner)
names every row with an open cell.

- **External prerequisites:** an operator at the desktop for the last Test Suite session
  (TS-Q01), the VMware guest, a V20 library input, RT Professional and other HMI targets, a
  physical PLC (B05), optional products and licenses such as SiVArc, Test Suite execution
  prerequisites and the licensed AX trace package (B04), a native trace route (B06),
  nonproduction Project Server, Teamcenter and UMC endpoints (B10), and installations and SDKs
  of other TIA versions (B11).
- **Recorded V21 boundaries:** Startdrive cannot read a drive object number added through Openness, the
  station upload's TLS challenges offer no certificate (trusted unauthenticated for owned PLCSIM only), an OPC UA reference-namespace import crashes TIA,
  force-table rows can only be authored in the
  TIA editor, and the
  CPU protection level needs a full-access password no route accepts.
- **In progress:** live monitoring and active forcing.
- **Rows blocked by decision:** five `unavailable_target` rows (31 cells:
  `change.call_connection_set`, `change.project_attributes_set`, `change.hmi_engineering` and
  the two OPC UA reference-namespace kinds) are blocked and do not count toward 1.0 (decision
  of 29 October; each row's disposition is in the ledger). The three block-protection kinds
  lacked only a repository route; building it is 1.0 work (decision of 29 October).
- **Deferred rows:** ten `not_implemented` rows (30 cells) are not open cells but stay 1.0
  work ([TODO](TODO.md#deferred-rows)).
- **Release mechanics:** one version source, a CHANGELOG, a release workflow, a pre-release
  security review, a contract freeze policy, the engineering package, tag and release notes
  ([TODO](TODO.md#release-mechanics-and-policy)).

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
[29 October](history/status-log.md#29-october-2026): online downloads to an owned PLCSIM instance, PLCSIM virtual time, interrupt injection, memory reset and storage, typed CPU parameters, device/project compile and the tag-table lifecycle were added and natively qualified except HMI compilation ([record](verification/plcsim-openness-gaps-20261029.md)); the runtime UA, web and capture tools passed
against the real servers of an owned TCP/IP PLCSIM instance ([record](verification/runtime-real-server-20261029.md)); the [MCP surface](reference/mcp-surface.md)
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
