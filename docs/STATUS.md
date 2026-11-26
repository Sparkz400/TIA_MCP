# Current status

TIA MCP is pre-1.0; use it only with offline projects, copies, scratch projects and
simulators you own (see the [README](../README.md)). Counts from catalog `0.5.5` (26 November 2026: the worker page totals cited; the follow-up checks of 14 to 25 November and a hardware smoke rerun; the V19 adapter requalified; the [native runs of 26 November](verification/native-runs-20261126.md) bound the project lifecycle, provisioning recovery, `sim_prune`, `sim_network_mode_set` and `hmi_content_fingerprint` rows; `0.5.0` was the MCP surface redesign of 0.9.0), computed from the catalog and the matrix on 26 November; backlog reconciled on 24 November, decisions of 25 November recorded:

| | |
| --- | --- |
| Feature catalog | `0.5.5` (26 November): 496 features, 463 verified, 33 unverified; 333 MCP tools ([catalog](../contracts/feature-catalog.v1.json)) |
| Release matrix rows | 116 shipped, 347 limited, 20 unqualified, 12 unavailable on V21, 1 descoped (out of 1.0 scope) |
| Matrix cells | 2,105 bound, **60 open**, 72 blocked, 3 descoped ([matrix](plans/release-matrix.v1.json)) |
| Release obligations | 0 of 44 accepted; the [TODO](TODO.md) was reconciled on 24 November against catalog `0.4.189` ([ledger](plans/release-obligations-20261015.md), [acceptance rules](plans/release-acceptance.md), [earlier review](verification/release-readiness-20261103.md)) |

Release scope: every feasible capability ships before 1.0; missing prerequisites and
unresolved feasibility stay release blockers ([release ledger](plans/release-obligations-20261015.md)). The one
exception is a maintainer scope decision: `drives.engineering` is out of 1.0 scope since 25 November.

The [3 November offline follow-up](verification/offline-backlog-20261103.md) adds selectable
PLCSIM SingleStep capture modes, bounded Web syslog reads and a retained MCP discovery
candidate. These additions do not promote native qualification or release acceptance.

Fresh local completion evidence: [2 November checks and exact prerequisites](verification/local-completion-20261102.md). The recovered vendor inputs, one V20 upgrade and the production FBD OB route now have fresh proof; this does not close the release ledger.

[3 November workstation review](verification/release-readiness-20261103.md): CFC/DCC V21 public
APIs and a V19 Portal/SDK are installed on the maintainer's second workstation. Since 9 November a
read-only V19 adapter (Host `--tia-version 19`) and V19-to-V21 upgrades are qualified there
([record](verification/tia-v19-adapter-20261109.md)); the CFC/DCC adapters and qualification remain open.
Earlier statements that these products are absent describe a different workstation.

## What is open

The Classic screen workflow adds capture, composition and bounded batch editing.
An owned two-screen RT Advanced fixture now passes native batch success, reverse-order
rollback, lost-response reconciliation and fresh reopen, with local timing recorded.
Its 13 catalog acceptance cells remain open: bounded fixture evidence does not establish
profile-wide, visual or runtime qualification. See the
[continuation record](verification/classic-screen-workflow-draft-20261103.md).

Of the rows added from 14 to 20 November without catalog evidence, the project save, close, archive and copy rows
(X01), provisioning status and recovery, `sim_prune` and `sim_network_mode_set` (L03) and `hmi_content_fingerprint` (X07) were bound natively on
26 November ([record](verification/native-runs-20261126.md)). Still open from that period: UMAC login (X01), the
WinCC Unified runtime alarm tools (X13), the Web API download, backup and alarm acknowledgement routes (D12; their
native run is blocked by the PLCSIM adapter's network setup), and the real-PLC online tools (D09).

[TODO](TODO.md) lists the open work; [open matrix rows by owner](TODO.md#open-matrix-rows-by-owner)
names every row with an open cell.

- **External prerequisites:** the VMware guest, other HMI targets, a physical PLC (B05), optional products and licenses such
  as SiVArc, Test Suite execution prerequisites and the licensed AX
  trace package (B04), a native trace route (B06), nonproduction Project Server, Teamcenter and
  UMC endpoints (B10), and installations and SDKs of other TIA versions (B11; V19 has a read-only adapter).
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
- **Descoped rows:** the last `not_implemented` row, `drives.engineering` (3 cells), was put out of 1.0 scope by the
  maintainer on 25 November: online access, download/upload, activation, security and safety need a physical drive
  (B05). Its ledger disposition is a scope decision, not a V21 boundary; its cells are `descoped`, neither open nor
  deferred, and not a pass ([TODO](TODO.md#deferred-rows)). No row is `deferred`.
- **Maintainer decisions of 25 November:** per-attempt TLS trust stays, a PLCSIM network-mode switch tool is to be
  built, fingerprint reuse stays off by default, the three lost library reports get a reviewer disposition and the
  missing native recheck of the watch-owned Test Suite reopen is a stated limitation; obligation acceptance is still
  open ([decisions](TODO.md#decisions-for-the-maintainer)).
- **Release mechanics:** product version 0.9.0 from one source, a CHANGELOG, a draft-only
  release workflow and the 30 October security review are in place, and the contract freeze policy was accepted on 8 November;
  its enforcement, the engineering package and the release itself remain ([TODO](TODO.md#release-mechanics-and-policy)).

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

Dated notes, newest first, are in the [status log](history/status-log.md); open work is in the [TODO](TODO.md).
The last weeks in short:

- **26 November:** [native runs](verification/native-runs-20261126.md) bound the project lifecycle, provisioning
  recovery, `sim_prune`, `sim_network_mode_set` and `hmi_content_fingerprint` rows; the V19 adapter requalified;
  the follow-up checks of 14 to 25 November and a hardware smoke passed (catalog `0.5.2` to `0.5.5`).
- **25 November:** `v0.9.0`, the first tagged pre-release, with the MCP surface redesign (catalog `0.5.0`); the
  [audit follow-ups](TODO.md#audit-follow-ups-25-november) and six maintainer decisions.
- **14 to 20 November:** user feedback on v0.9.0 ([14 November](history/todo-closed-20261126.md#user-feedback-on-v090-14-november),
  [18 November](history/todo-closed-20261126.md#user-feedback-on-v090-18-november)) and the
  [20 November field findings](TODO.md#field-findings-from-a-physical-download-20-november) added the project
  lifecycle tools, provisioning status and recovery, the WinCC Unified runtime alarm tools, the real-PLC online tools
  and the UMAC project login; PRs 315–326 hardened the safety write gates, the runtime clients and the stores.
- **4 to 12 November:** the [offline engineering session follow-ups](history/todo-closed-20261126.md#offline-engineering-session-follow-ups-4-november)
  were qualified natively, and the read-only V19 adapter was added (9 November).
- **Up to 3 November:** the work that built the verified catalog, from the 15 October scope policy on, is in the
  [status log](history/status-log.md#3-november-2026).

## Updating this page

When a change lands, add its dated note at the top of the
[status log](history/status-log.md) under its date heading, then update the lists above.
`python -B scripts/status_counts.py --write` updates the counts in the table and in the TODO
from the catalog and the matrix; the offline suite fails while they differ. Keep this page
short; detail belongs in the log, `verification/` and the catalog.
