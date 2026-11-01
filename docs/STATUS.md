# Current status

TIA MCP is pre-1.0; use it only with offline projects, copies, scratch projects and
simulators you own (see the [README](../README.md)). On `main`, 1 November 2026:

| | |
| --- | --- |
| Feature catalog | `0.4.92`: 479 features, 454 verified, 25 unverified; 325 MCP tools ([catalog](../contracts/feature-catalog.v1.json)) |
| Release matrix rows | 129 shipped, 325 limited, 16 unqualified, 7 unavailable on V21, 2 deferred |
| Matrix cells | 2,067 bound, **96 open**, 37 blocked, 6 deferred ([matrix](plans/release-matrix.v1.json)) |
| Release obligations | 0 of 44 accepted ([ledger](plans/release-obligations-20261015.md), [how acceptance works](plans/release-acceptance.md)); refreshed on 28 October against catalog `0.4.68`, D02, D04, D06 and X04 on 29 October against `0.4.70`, C03, D12 and D16 against `0.4.72`, D09, F00, L01, L03, D02, X01, X04, X07, X08, X10 and X11 against `0.4.76`, and D10 against `0.4.92` |

Release scope: every feasible capability ships before 1.0; missing prerequisites and
unresolved feasibility stay release blockers ([release ledger](plans/release-obligations-20261015.md)).

## What is open

[TODO](TODO.md) lists the open work; [open matrix rows by owner](TODO.md#open-matrix-rows-by-owner)
names every row with an open cell.

- **External prerequisites:** an operator at the desktop for the last Test Suite session
  (TS-Q01); the VMware guest, a V20 library input, RT
  Professional and other HMI targets, a physical PLC (B05), optional products and licenses such
  as SiVArc, STEP 7 CFC, SINAMICS DCC, Test Suite execution prerequisites and the licensed AX
  trace package (B04), a native trace route (B06), nonproduction Project Server, Teamcenter and
  UMC endpoints (B10), and installations and SDKs of other TIA versions (B11).
- **Recorded V21 boundaries:** no public API forces a value (only the TIA Portal UI does), the
  station upload's TLS challenges offer no certificate (trusted unauthenticated for owned PLCSIM only), an OPC UA reference-namespace import crashes TIA,
  force-table rows can only be authored in the
  TIA editor, and the
  CPU protection level is refused while UMAC runtime access is enabled (its password now has a route).
  Since 31 October also: the PLC's OPC UA server has no `HistoryRead`, no public member moves a
  PLC block, Openness cannot install a GSD file, and a safety program cannot be loaded into PLCSIM (the offline
  memory-card download is refused, the online route needs a card from the PLC).
- **Rows blocked by decision:** seven `unavailable_target` rows (37 cells:
  `change.call_connection_set`, `change.project_attributes_set`, `change.hmi_engineering`,
  the two OPC UA reference-namespace kinds, `block.move` and `safety.logic_changes`) are blocked and do not count toward 1.0 (decision
  of 29 October, the last two on 1 November; each row's disposition is in the ledger). The three block-protection kinds
  left the blocked set: their credential route was built and verified on 30 October.
- **Deferred rows:** two `not_implemented` rows (6 cells) are not open cells but stay 1.0
  work ([TODO](TODO.md#deferred-rows)).
- **Release mechanics:** product version 0.9.0 from one source, a CHANGELOG, a draft-only
  release workflow and the 30 October security review are in place; a contract freeze policy,
  the engineering package and the release itself remain ([TODO](TODO.md#release-mechanics-and-policy)).

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

Dated notes, newest first, are in the [status log](history/status-log.md). From 31 October to
[1 November](history/status-log.md#1-november-2026): project upgrade, OPC UA method calls and
conditions, external Git delivery with two-sided conflicts, the Unified text-list import, a
restricted translation profile, four more combined-apply shapes, technology version and F-parameter
profiles and the shared-device/MRP hardware topology were added and natively qualified (catalog
0.4.88); HistoryRead, block move, GSD installation and loading a safety program into PLCSIM are
recorded V21 boundaries. Later on 1 November: the `project_upgrade` interruption cases passed natively
(16/16) and combined apply gained a multi-instance shape. Drive objects whose number Startdrive cannot read
are addressed by container index, and the three D10 drive rows passed natively on a G120C PN; `drive_observe` is
verified, the two change kinds wait on a rerun that checks `change_status` (catalog 0.4.92; the drive object number
left the recorded boundaries). In a desk session the Explorer roundtrip passed
in VS Code 1.144.0, change.hmi_compile on desk-configured Classic and Unified fixtures and an
operator-installed I-device GSD in the hardware runner (catalog 0.4.89). On
[31 October](history/status-log.md#31-october-2026): the 20 findings of a code audit were fixed
offline, among them simulator ownership on every write, a PLCSIM route check for runtime writes and
stores that no longer block on a missing old path; the changed native paths await a native recheck. On
[30 October](history/status-log.md#30-october-2026): software-unit rename and create-from, motion
connections, five PLCSIM signal and observation tools, Test Suite application runs (TS-Q02), MCP
resource templates, completions and logging, the block-protection credential route, the CPU
protection-level route and default-on combined library apply were added and natively qualified
(catalog 0.4.86). On
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
