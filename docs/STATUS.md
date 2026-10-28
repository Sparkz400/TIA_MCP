# Current status

TIA MCP is pre-1.0; use it only with offline projects, copies, scratch projects and
simulators you own (see the [README](../README.md)). On `main`, 28 October 2026:

| | |
| --- | --- |
| Feature catalog | `0.4.66`: 450 features, 354 verified, 96 unverified ([catalog](../contracts/feature-catalog.v1.json)) |
| Release matrix rows | 121 shipped, 233 limited, 80 unqualified, 6 unavailable on V21, 10 deferred |
| Matrix cells | 1,617 bound, **420 open**, 30 deferred ([matrix](plans/release-matrix.v1.json)) |
| Release obligations | 0 of 44 accepted ([ledger](plans/release-obligations-20261015.md)); its per-obligation fields were last refreshed on 27 October against catalog `0.4.30` |

Release scope: every feasible capability ships before 1.0; missing prerequisites and
unresolved feasibility stay release blockers ([release ledger](plans/release-obligations-20261015.md)).

## What is open

[TODO](TODO.md) lists the open work; [open matrix rows by owner](TODO.md#open-matrix-rows-by-owner)
names every row with an open cell.

- **External prerequisites:** real OPC UA and web servers of an owned PLCSIM instance in
  TCP/IP mode (runtime UA, web and capture rows), Test Suite execution, the VMware guest,
  a V20 library input, RT Professional and other HMI targets, and a physical PLC.
- **Recorded V21 boundaries:** Startdrive drives expose no device compile service, the
  online compare, upload and DB-snapshot routes need a PLC login they never perform, an
  OPC UA reference-namespace import crashed TIA, Unified text lists have no import route,
  and force-table rows can only be authored in the TIA editor.
- **Rows that cannot close on V21:** six `unavailable_target` rows hold 37 open cells
  (`change.call_connection_set`, `change.project_attributes_set`, `change.hmi_engineering`
  and the three block-protection kinds). They need a disposition before every cell can close.
- **Release mechanics:** the engineering package, version, tag and release notes.

## Where the evidence is

The [documentation index](README.md#evidence-and-status) explains the chain from catalog
claim to raw report. In short: each verified feature cites curated summaries in
[`evidence/`](../evidence/README.md); their raw reports are kept in the main checkout's
ignored `artifacts/evidence/` store and checked by `scripts/audit_evidence_store.py`.
84 raw reports of 74 evidence IDs were gone before that store existed
([list](../evidence/raw-reports-lost.json)); 140 of the 354 verified features cite only
those IDs, so their runs cannot be re-audited.

## Recent changes

Dated notes, newest first, are in the [status log](history/status-log.md). On
[28 October](history/status-log.md#28-october-2026): raw evidence store and encoder test
leak fix; runtime real-server Tasks 1–3; project translations under V21 Update 3; the
safety F-block import with a STEP 7 Safety license; the composite rebind plan; I-device
transfer areas and GSD export; watch-table lifecycle and Host-loss reconciliation;
graphical network edits; connections, GRAPH, VCI mappings and CAx.

## Updating this page

When a change lands, add its dated note at the top of the
[status log](history/status-log.md) under its date heading, then update the numbers
and lists above. Keep this page short; detail belongs in the log, `verification/` and
the catalog.
