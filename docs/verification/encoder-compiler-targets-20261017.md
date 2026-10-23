# Encoder compiler target identities — 17 October 2026

## Reproduced defect

The retained synthetic reproduction in `artifacts/encoder-compiler-next-01`
changed every non-device compiler target to `/unrelated/plc` and still passed
the production encoder prerequisite validator. Hardware device inventory checks
were already present; scratch, software edit, recovery and seeder coverage were
not bound to independently selected targets. This was a portable validator
defect, not an executed Siemens qualification.

## Repair

The qualifier now captures complete, warning-free project inventories with exact
project/process ownership and a unique PLC/hardware identity before selections.
It sends explicit project and PLC selectors to preparation and reads, including
recovered and reopened copies. Scratch and software compile receipts must match
that PLC and cannot substitute a device compiler policy with matching text.

The seeder now selects authorized CPU/module paths under exclusive access. Its
device set comes from the original independent inventory after hardware setup.
The shared bounded `DeviceCompilation` helper covers root, ungrouped and nested
devices, including remote-I/O and empty devices. The seeder checks the authorized
set before mutation and again before compilation; every compiler service is
resolved before the first compiler dispatch. Post-compile identities are checked,
and unknown outcomes are never replayed. Its hardware traversal and compiler
diagnostics are bounded. Existing worker and library callers use the same helper;
software diagnostic rendering also shares its bounded implementation.

The exact seeder stdout byte count/hash is captured when its outcome arrives.
Later validation rechecks owned retained output and authorization, original call
boundaries, contiguous inventory requests, and the full pre/post device set.
Preparation IDs must be unique and belong to their original session. Edit,
recovery and native phase summaries must match original calls and owners.
The read-only companion captures inventory before reading and its independent
auditor checks the original selectors, parameter-object paths and ownership.

New reports use `encoder-exact-compiler-targets-v1`. Older reports are preserved;
they cannot satisfy current target admission merely by carrying a lifecycle
policy or successful diagnostic counts. The auditor exposes
`compilerTargetCoverageVerified` separately from diagnostic inspection.
Historical evidence profiles and all 106 unavailable binding records are retained.

## Documentation and limits

Context7 resolved Siemens' V20 Openness documentation. The official
[compilation reference](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-projects-and-project-data/compiling-a-project)
distinguishes device hardware/software from PLC-software compilation. That V20
reference and linked synthetic API shapes do not establish V21 ABI compatibility.
No Siemens SDK, native worker, seeder, PLCSIM, PLC or motion was executed here.

The recovery compile still follows the existing same-value parameter operation;
it is not a fresh pre-edit baseline compile. Independent recovery-copy endpoint
correspondence, closed-project admission, full unrelated-content preservation,
native attribute/channel behavior and TO-03–12 remain separate open work.

## Verification

Targeted general checks pass 219/219, including four new groups for exact hardware
paths, ambiguity/cycles/bounds, independently authorized device sets and compiler
diagnostic bounds. The encoder lifecycle suite passes 27 methods, exercising
successful wrong-target receipts at runtime and altered original inventories,
selectors, summaries, phases and retained seeder output before companion startup.
Pure compiler/selection tests include malformed ownership, duplicate PLCs, missing
hardware and device-policy substitution. Optimization checks keep runtime guards
active.

An intermediate optimized run exposed a synthetic module path named `Module`
while its row claimed `TiaMcp_EncoderHardware`. The fixture was corrected to the
native path convention; the stronger production check was retained. The failed
log remains at `artifacts/encoder-targets-lifecycle-optimized-01.log`.

Final frozen Git tree: `92e17fed1de61ab210ceeb2fc25f5c8e9d707095` (2024 files).
Source manifest (`artifacts/encoder-targets-02/source-manifest.json`) SHA-256:
`47575b37ed69f872f071765ed1cda96bbbfacf7b03152db448f9d894815366f0`.
Source ZIP SHA-256: `0028b9b8fccfc4fff7d37fd6acd4e2878a9cce867d178ae29325bf7d33b80965`.

The aggregate (`artifacts/encoder-targets-02/source/artifacts/checks/result.json`)
passes 41 managed suites, 1,406 Python tests (one unavailable-evidence skip),
24 Explorer model/client checks, contracts/source bindings and all offline MCP
workflows. The general executable passes 219 checks, including the linked shared
device compiler and hardware traversal. Targeted optimization runs pass 27 encoder
lifecycle methods and 13 compiler/selection methods under -OO.
Run: 2026-10-17T13:05:33.6736771Z to 2026-10-17T13:16:59.6702667Z.
Aggregate receipt SHA-256: `3a6f5728d75c096b3614a51670700e9faaf13d36b3eff7cd6cdecf9c7c98a453`.
Console evidence (`artifacts/encoder-targets-02/aggregate-console.log`).

All 24 package stages (`artifacts/encoder-targets-02/source/artifacts/package-checks/result.json`)
pass. Bundle `tia-mcp-offline_20261017_130600_08c4e936` contains 1430 manifest files.
Package receipt SHA-256: `cb020ef3d22689f2f59cc97c5f98289139ee9aa1837286ede3fe6afb662b1176`.
Manifest SHA-256: `69cb4ed52efb257652bc1ed3e27521ac05f2a7afc1a43adec7cc85f89aa00eab`.
ZIP SHA-256: `79b3ae6f6b99b63b1dbc950ea36770d680bfb1eeb5a09a75e8cddc5e899cfc6f`.
The portable package disables native workers. Browser tests were not repeated
for this compiler/harness increment; SDK/native compilation remains unexecuted.

The source comparison (`artifacts/encoder-targets-02/final-source-comparison.json`)
verifies the frozen source remained unchanged and permits only completion docs
to differ in the final commit. All 106 unavailable historical evidence binding
rows remain unchanged. The integration receipt (`artifacts/encoder-targets-02/integration.json`)
records the pushed commit, exact hosted CI outcome and cleanup.

This increment does not complete the full 44-workstream goal or qualify the
changed native compiler/seeder. Fresh baseline compilation and the wider queue
remain active.
