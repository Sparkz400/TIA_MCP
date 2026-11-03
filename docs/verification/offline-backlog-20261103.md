# Offline runtime and contract-inventory follow-up — 3 November 2026

This follow-up starts from merged PR 206 (`30967af7`). Desktop control remained stopped.
No TIA Portal, simulator instance, physical PLC or VMware guest was started. Catalog
`0.4.133` retains the same qualification states: 28 open cells, six deferred cells,
72 V21 boundary cells and 0/44 accepted release obligations.

## Completed implementation

- **Capture modes:** `simulator.syncMode` selects SingleStep_C/P/CP/CT/CPT when
  `cycleSynchronous` is true. Omission keeps the original SingleStep_C behavior and
  serialized plan bytes. The worker checks the selected mode before each advance;
  existing ownership, bounded waits and restore-on-stop/failure/shutdown remain.
  A selected sync point need not represent a whole scan. CT/CPT use the existing
  overwritten minimum cycle time. Bus/timespan capture modes remain unsupported.
- **Web syslog:** `runtime_web_diagnostics`, kind `syslog`, requests 1–20 entries
  through the existing pinned-TLS session. Unadvertised methods do not dispatch.
  Malformed bounds and response shapes refuse; provider denials remain explicit and
  omit remote error text. Raw entries and lifetime/loss counters are retained, while
  truncation and lossless-history claims remain unknown/false. R/H redundancy selection,
  historical offsets, service/profiling downloads and acknowledgment are not added.
- **Contract inventory:** the existing MCP surface runner now retains `surface.json`
  with actual normal-Host discovery, schemas, annotations, prompts, resources,
  negotiated protocol and Host/Core hashes. The report pins its digest, and CI uploads
  both. This supplies a reproducible candidate for the draft policy; it neither accepts
  the 13 policy decisions nor freezes the API.

## Validation

- Simulator worker builds against the installed V8 SDK, with no runtime API invocation.
- 314/314 general managed checks pass.
- Physical-runtime checks pass, including 54 PLCSIM capture-source checks with a fake
  worker. These cover allowed/refused modes, retention, changed-request refusal and
  fresh reopen without acquisition replay. Explicit CP/CT/CPT stop/failure/shutdown
  restoration is also checked in the complete regression.
- Actual Host Web workflow: 1,633 checks pass using disposable loopback TLS and current-user
  DPAPI, with no OS trust changes. Syslog success, invalid bounds, denied access and
  unadvertised capability are exercised through MCP.
- Actual Host discovery: 156 checks pass. The normal Host advertises 307 tools,
  87 output schemas, four prompts, one concrete resource and three resource templates.
  Catalog inventory additionally includes 20 hidden qualification tools.
- Contract coverage, generated matrix/reference and implementation bindings pass.
  The binding validator retains the six known unavailable VM artifact paths.
- Complete offline regression: pending in `artifacts/offline-backlog-validation/result.json`.

Reports are under `artifacts/offline-backlog-*`; the final discovery snapshot is
`artifacts/offline-backlog-surface/surface.json`. Intermediate test failures are retained:
the initial new capture test reopened its still-owned store, and the initial syslog harness
attempted a second session on an already-owned endpoint. Both existing ownership guards
correctly refused; the tests now close the first owner before opening the next.

## Documentation and remaining work

Capture semantics follow the installed S7-PLCSIM Advanced V8 API manual, 11/2025,
A5E53246567-AE, pages 176–178 and 204. The Context7 CLI did not launch on this workstation.
The [V21 Syslog method](https://docs.tia.siemens.cloud/r/simatic_s7_1500_et_200mp_manual_collection_enus_21/comprehensive-information/cpu-function-manuals/web-server/api-application-programming-interface/reading-diagnostics-and-service-data/syslog.browse)
defines the count bound, counters and permission. Its count examples contradict its
parameter table; the implementation follows the table's stricter maximum of 20 entries.

Native qualification of these additions remains open, alongside TS-Q03, broader HMI,
physical runtime and VMware qualification. Remaining software work includes acyclic data
records, timespan/bus capture modes, ticketed service/profiling downloads, optional-product
adapters and policy-dependent compatibility enforcement. The additions do not resolve those
separate contracts or their native acceptance requirements.
