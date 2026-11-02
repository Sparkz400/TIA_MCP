# Local completion checks — 2 November 2026

This record separates completed work on this PC from vendor, operator and equipment prerequisites. It does not approve a 1.0 release or qualify a physical installation. Raw reports, logs, frozen source archives and binaries are retained under `artifacts/local-completion-20261102/`, `artifacts/evidence/` and the short native-run root `D:/tq1102/`.

## Library and component checks

- Seven original PAL/DMC ZIPs were recovered with their recorded hashes, totaling 467,587,713 bytes. The native vendor intake rerun passed (`local.library_vendor.rerun.20261102`). These replacement runs do not recover missing historical reports.
- The V14 SP1–V18 upgrade corpus and one V20 LGF library passed upgrade, archive and reopen with complete version identities and unchanged source hashes (`local.library.upgrades-v20.20261102`, 41 stages). The [V20 receipt](vendor-intake-v20-20261102.json) identifies the downloaded bytes; it is not publisher authentication. V19 is covered by the vendor rerun. Firmware-target mismatches remain separate.
- DMC prerequisites and three default FBs compile with exact native library links on CPU1511 V4.1 (`local.library.dmc-order.20261102`). The selected consumer `fbAirlock_Motor` and dependency `fbMotor_Reversing` exposed two defects: selected dependencies must instantiate first, and existing reviewed destination paths must be preserved when importing their consumers. Both are fixed; the native pair passes.
- The all-default DMC probe selected 218 versions and successfully instantiated the first batch of 100, then correctly stopped on compiler errors (`local.library.dmc-vendor-compile-boundary.20261102`, failed evidence). Diagnostics name protected `fbSiwarexWP321Limited_SIMATIC` and Serialize/Deserialize V2.0 conflicts, missing or mismatched `fcSetHMIStatusSimulation_1` in `fbVFD_V20`, and S7-1200 MB_COMM_LOAD/MB_MASTER instructions on the S7-1500 test CPU. Completing that corpus needs a compatible vendor library/authorized protection password and CPU-specific selection. No full-library or runtime qualification is claimed.
- A fresh component with a new global DB inserts after the last network of an existing FBD OB. Candidate qualification and the normal production Host both passed preservation, compilation, reopen and checkpoint recovery (`local.component.ob.20261102`, `local.component.ob-production.20261102`). Other OB combinations, software units and embedding-parent effects remain gated.

## Simulator and release controls

The output-array write guard passed unchanged-output checks for individual and batch writes (`local.plcsim.output-arrays.20261102`). IP-change observation passed while STOPped, with the original IP restored before RUN (`local.plcsim.signals-ip.20261102`). Configuration-change event delivery is not established by that IP test.

The normal TCP/IP runtime corpus passed (`local.runtime.plcsim-tcpip.20261102`): provisioning, Web/OPC UA connections, subscriptions, captures, stale-pin refusal and clean shutdown/release. The first attempt stopped because another owned qualification process exited during its isolation check; the isolated rerun passed. PLCSIM was restored to its original Softbus mode with no active instances and strict motion timing still enabled.

The 87-stage F-I/O tag/VCI/master-copy/unit route corpus passed (`local.safety.fio-routes.20261102`), including the added merged-conflict and unit-copy cases. Its separate absolute-source probe failed: native standard SCL generation changed a tag table. Standard source, XML/graphical and project-ward VCI block imports now check absolute I/O references against the selected PLC's F-I/O ranges at prepare and apply. Source scanning conservatively includes comments and quoted text; unknown XML I/O widths/offsets refuse on a PLC owning F-I/O. This guards implicit tag creation and is not functional-safety qualification.

Release acceptance now checks reviewer role, date, commit ancestry, matrix hash, owned-row evidence and explicit blocker dispositions. A later change to reviewed inputs invalidates acceptance. The ledger still has no accepted obligations; these checks do not turn a self-declared reviewer into an authenticated signature.

## Test Suite operator seed

With the user's approval, an owned copy of the scratch project was opened in the TIA V21 interface and a minimal system test was created and saved at `D:/tq1102/ui-seed/TiaMcpScratch_20261102_201944_5eab6efb.ap21`. It reads `TiaMcp_EnableGate_DB.Active` and asserts FALSE. The source scratch was left intact and the UI session was closed after saving. The seed is exploratory: its endpoint scope, anonymous access, certificate trust and execution are not qualified.

The next operator action is to grant the seed CPU's anonymous user only the required `OPCUAServerAccess` right in TIA and establish Test Suite's server-certificate trust in an isolated simulator setup. The computer-use skill forbids automating in-app security settings. The system-test qualification mode TS-Q03 also remains software work; creating the seed does not implement or validate it. See the [system-test design and boundary](testsuite-system-blocker-20261101.md).

## Remaining work and prerequisites

These are separate from green automated checks:

- **Operator decisions:** contract-freeze policy choices and the 44 release-obligation reviews, including explicit dispositions for V21 API boundaries and incomplete preservation. No approvals are fabricated by this change.
- **Supplied fixtures or access:** older TIA installations/SDKs and protected or locked V14–V20 project fixtures; the VMware guest and its account/prerequisites; nonproduction Project Server, Teamcenter and UMC endpoints/accounts; missing historical native05 and other original reports if their audits are required.
- **Products and equipment:** optional SiVArc/CFC/DCC/AX trace products and licenses; additional HMI targets/runtime; an isolated physical PLC and I/O; drive and safety hardware with qualified commissioning personnel. Simulator or offline evidence cannot replace these.
- **Further local development/qualification:** TS-Q03 and its result profile, broader component and HMI preservation routes, remaining simulator event/data-record/cycle modes, residual Web API routes, API semantic mapping and contract-policy closure. These are software backlog, not requests for equipment. The detailed [TODO](../TODO.md) and [release matrix](../plans/release-matrix.v1.json) remain authoritative.
