# Encoder configured-hardware peer preservation — 17 October 2026

## Change

The encoder qualification now inspects every hardware item in its complete owned
project inventory, including unrelated CPU and I/O items. Each exact-path read
must match its independently inventoried owner, name, type identifier,
classification, slot and address ranges. Missing/duplicate/foreign results,
partial or opaque attributes and unresolved network references refuse. Responses
are validated immediately, before dispatching the next read.

Captured state includes configured attributes, address bit lengths and byte spans,
channel identities and attributes, network nodes/subnets, controller I/O systems
and connector associations. Project context is normalized only after ownership
and reference membership are checked. Original values and unknown additional
fields remain in comparisons; timestamps in the read envelope are not hardware
settings. Network absence requires an explicit native `notExposed` observation.

The selected endpoint inventory, fresh pre-read inventory and post-read inventory
must agree. The two earlier encoder endpoint reads must agree with the matching
full hardware reads. Inventories and all captured settings must remain unchanged
across edit, recovery, both saved reopens and both fresh inspection copies.
This is a bounded capture, not an atomic snapshot.

The independent audit replays the original ordered requests/results immediately
after the already verified software capture. Exact request selectors are bound
to their returned items; merely returning the right total set is insufficient.
Source admission requires the current hardware policy before Host startup.
Older source reports remain diagnostic evidence with
`configuredHardwarePeersVerified: false`; they do not authorize current copied
inspection. `fullProjectPreservationVerified` remains false.

## Native response repair

Source review found that `project_inventory` omitted `typeIdentifier`, although
the existing encoder endpoint guard required it and the synthetic fixture supplied
it. The native response now includes that exposed property. `hardware_inspect`
returns the same type identifier and an explicit network observation state.
The actual SDK-dependent methods have not been compiled or run here.

## Verification

Thirteen hardware regression methods use production qualification, admission,
recovery and independent audit code with five populated synthetic hardware items,
real files and fake native processes. They cover attributes/value kinds, address
ranges, channels, network nodes/controllers/connectors, service changes,
add/remove, foreign owners, opaque/excluded/missing values, ambiguity, mid-capture
drift, recovery and saved/copy reopen drift, original-call/summary tampering,
selector substitution, immediate refusal and legacy admission. Read-envelope
timestamps intentionally differ while unchanged configuration passes.

Final frozen Git tree: `b11a9802bdf1878a721baa6f270f161f459191b8` (2041 files).
[Source manifest](../../artifacts/encoder-hardware-01/source-manifest.json) SHA-256:
`a0bf83c1a17e33414b8a7c7de1ff1fe8c93f448f9cdffcb595613fb9616e8175`.
Source ZIP SHA-256: `95c41663f040e66ddde8795f53db0439b9d97b53e0b81a059223e45e556c9fb8`.

The [aggregate](../../artifacts/encoder-hardware-01/source/artifacts/checks/result.json)
passes 41 managed suites, 1,455 Python tests (one unavailable-evidence skip),
24 Explorer model/client checks, contracts/source bindings and all offline MCP
workflows. The general executable passes 226 checks. All 93 encoder methods also
pass under -OO, including thirteen hardware-preservation methods with populated peers.
Run: 2026-10-17T15:36:02.3271869Z to 2026-10-17T15:49:20.6457459Z.
Aggregate receipt SHA-256: `0b462f7e83a8c0e978160880e120aeccab2297cf64d0c21cb17586211d2b706f`.
[Console evidence](../../artifacts/encoder-hardware-01/aggregate-console.log).

All 24 [package stages](../../artifacts/encoder-hardware-01/source/artifacts/package-checks/result.json)
pass, including exact ZIP/manifest verification and relocated MCP workflows.
Bundle `tia-mcp-offline_20261017_153628_3f885c4c` contains 1443 manifest files.
Package receipt SHA-256: `6f68159ccb6b22f647c4769c9c59b5d8a3f6cd3da341df14fa38aa592db8b05a`.
Manifest SHA-256: `92188052074804937f8472cba42b4d7c7757956f537d80007ed2e4b18402a90c`.
ZIP SHA-256: `0cbae06314623360ade51e898920f7ffb8b40a84f515b4cd234dc669bc076721`.
Browser tests were not repeated for this qualification-harness/inventory change.

The [source comparison](../../artifacts/encoder-hardware-01/final-source-comparison.json)
verifies unchanged tested implementation; only five completion documents differ.
All 106 unavailable
historical evidence binding rows remain unchanged. The
[integration receipt](../../artifacts/encoder-hardware-01/integration.json) records
Git/CI and cleanup state.


## Remaining limits and next work

Only the exposed configured DeviceItem observations are covered. Unreadable or
excluded attributes refuse this qualification; no exception is invented for them.
Device-level/project settings, empty device groups, external source documents,
native system-group structure, unexposed services, HMI/libraries and other
project content require separate inventory/preservation coverage. Hardware-wide
observed settings are not proof of physical wiring or runtime behavior.

Continue the exact remaining coverage inventory and TO-03–12, SA-01–12 and the
full 44-workstream queue. Native SDK/product behavior, actual mutation/recovery,
physical hardware and release acceptance remain unqualified. All 106 missing
historical evidence binding records remain unchanged.
