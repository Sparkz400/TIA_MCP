# Current status

TIA MCP is pre-1.0; use it only with offline projects, copies, scratch projects and
simulators you own (see the [README](../README.md)). Catalog and backlog reviewed on 3 November 2026:

| | |
| --- | --- |
| Feature catalog | `0.4.156`: 491 features, 475 verified, 16 unverified; 330 MCP tools ([catalog](../contracts/feature-catalog.v1.json)) |
| Release matrix rows | 133 shipped, 338 limited, 6 unqualified, 12 unavailable on V21, 2 deferred |
| Matrix cells | 2,163 bound, **28 open**, 72 blocked, 6 deferred ([matrix](plans/release-matrix.v1.json)) |
| Release obligations | 0 of 44 accepted; backlog reconciled on 3 November against catalog `0.4.130` ([ledger](plans/release-obligations-20261015.md), [acceptance rules](plans/release-acceptance.md), [review](verification/release-readiness-20261103.md)) |

Release scope: every feasible capability ships before 1.0; missing prerequisites and
unresolved feasibility stay release blockers ([release ledger](plans/release-obligations-20261015.md)).

The [3 November offline follow-up](verification/offline-backlog-20261103.md) adds selectable
PLCSIM SingleStep capture modes, bounded Web syslog reads and a retained MCP discovery
candidate. These additions do not promote native qualification or release acceptance.

Fresh local completion evidence: [2 November checks and exact prerequisites](verification/local-completion-20261102.md). The recovered vendor inputs, one V20 upgrade and the production FBD OB route now have fresh proof; this does not close the release ledger.

[3 November workstation review](verification/release-readiness-20261103.md): CFC/DCC V21 public
APIs and a V19 Portal/SDK are installed here. Their license, adapter and native qualification gates
remain open. Earlier statements that these products are absent describe a different workstation.

## What is open

The Classic screen workflow adds capture, composition and bounded batch editing.
An owned two-screen RT Advanced fixture now passes native batch success, reverse-order
rollback, lost-response reconciliation and fresh reopen, with local timing recorded.
Its 13 catalog acceptance cells remain open: bounded fixture evidence does not establish
profile-wide, visual or runtime qualification. See the
[continuation record](verification/classic-screen-workflow-draft-20261103.md).

[TODO](TODO.md) lists the open work; [open matrix rows by owner](TODO.md#open-matrix-rows-by-owner)
names every row with an open cell.

- **External prerequisites:** the VMware guest, other HMI targets, a physical PLC (B05), optional products and licenses such
  as SiVArc, Test Suite execution prerequisites and the licensed AX
  trace package (B04), a native trace route (B06), nonproduction Project Server, Teamcenter and
  UMC endpoints (B10), and installations and SDKs of other TIA versions (B11).
- **Recorded V21 boundaries:** no public API forces a value (only the TIA Portal UI does), the
  station upload's TLS challenges offer no certificate (trusted unauthenticated for owned PLCSIM only), an OPC UA reference-namespace import crashes TIA,
  force-table rows can only be authored in the
  TIA editor, and the
  CPU protection level is refused while UMAC runtime access is enabled (its password now has a route).
  Since 31 October also: the PLC's OPC UA server has no `HistoryRead`, no public member moves a
  PLC block, Openness cannot install a GSD file, and a safety program cannot be loaded into PLCSIM (the offline
  memory-card download is refused, the online route needs a card from the PLC). Since 1 November: V21 Openness neither creates nor
  exposes WinCC RT Professional ([record](verification/hmi-rt-professional-20261025.md#v21-boundary--1-november-2026)), and
  V21 Update 3 cannot re-check a Test Suite style rule's scope at execution time, so production style execution is
  unavailable ([record](verification/testsuite-style-q01-20261101.md#v21-boundary)).
- **Rows blocked by decision:** twelve `unavailable_target` rows (72 cells:
  `change.call_connection_set`, `change.project_attributes_set`, `change.hmi_engineering`,
  the two OPC UA reference-namespace kinds, `block.move`, `safety.logic_changes` and the five Test Suite style execution
  rows) are blocked and do not count toward 1.0 (decision
  of 29 October, the last seven on 1 November; each row's disposition is in the ledger). The three block-protection kinds
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

Dated notes, newest first, are in the [status log](history/status-log.md). On
[2 November](history/status-log.md#2-november-2026) the worker paths of the 31 October audit ran natively (catalog 0.4.114 and 0.4.116):
the delete and rename compile rule, the unresolved-change refusal of `change_prepare`, generated-source reads, `vci_workspace_inspect`'s `contentSha256` and translation scratch
status, with the restricted translation pipeline running end to end again after an RFC 8785 fix of its admission scripts.
From 31 October to
[1 November](history/status-log.md#1-november-2026): the Classic screen/tag-table import and BackColor (TP700 Comfort) and PLC-backed
Unified tags (Unified Comfort, Unified PC) passed the HMI preservation gate natively and are admitted in production, and WinCC RT
Professional became a recorded V21 boundary (catalog 0.4.92). Before that: project upgrade, OPC UA method calls and
conditions, external Git delivery with two-sided conflicts, the Unified text-list import, a
restricted translation profile, four more combined-apply shapes, technology version and F-parameter
profiles and the shared-device/MRP hardware topology were added and natively qualified (catalog
0.4.88); HistoryRead, block move, GSD installation and loading a safety program into PLCSIM are
recorded V21 boundaries. Later on 1 November: the six TS-Q01 Test Suite style cases passed natively with the operator at the desk, which verifies three
style-job tools, and production style execution became a V21 boundary (catalog 0.4.97); the `project_upgrade` interruption cases passed natively
(16/16) and combined apply gained a multi-instance shape. Two admission gaps were closed (catalog 0.4.103) and rechecked natively with the symbolic output-write refusal (catalog 0.4.105): a first motion object without compile and F-I/O tag addresses from VCI packages and library master copies now refuse before any write; a second round (catalog 0.4.107) added the F-I/O rule to `software_unit_create_from`, made a renamed master-copy tag fail its change instead of reporting applied, and ran the remaining F-I/O, output-tag and canonical-hash checks natively. A TS-Q02 run queued the fixture case with `testsuite_style_job_prepare` and dispatched that job, which verifies the tool (catalog 0.4.108); system tests stay open, since they need PLCSIM's TCP/IP mode and a guest-access seed at the desk ([record](verification/testsuite-system-blocker-20261101.md), catalog 0.4.109). The Host and the worker now hash with one canonical form, RFC 8785 (catalog 0.4.104). Drive objects whose number Startdrive cannot read
are addressed by container index, and the three D10 drive rows are verified on a G120C PN (catalog 0.4.95; the
drive object number left the recorded boundaries). In a desk session the Explorer roundtrip passed
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
