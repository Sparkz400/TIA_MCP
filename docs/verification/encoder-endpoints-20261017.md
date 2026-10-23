# Encoder recovery-copy endpoint correspondence — 17 October 2026

## Repair

The previous qualifier compared complete provider-owned reference dictionaries.
A recovered project correctly supplies its own project path and may supply new
provider IDs, so literal equality rejected valid copied connections. The lifecycle
fixture mocked that comparison and could not detect the defect.

The native reference reader now reuses the bounded project hardware traversal to
prove each referenced DeviceItem belongs to the selected project. It emits the
canonical escaped hardware path, membership basis, status and item count alongside
the original provider roundtrip. Missing, ambiguous, aliased or cyclic membership
refuses a complete reference. Hardware traversal has its own 50,000-item bound;
getter counts remain separate. Neither observation is an atomic snapshot.

The shared Python endpoint check validates each observation against a complete
independent project inventory, exact PLC/process/project owner, immediate module
and channel-owner relationship, metadata and two exact hardware inspections. It
requires a unique typed channel, complete captured channel attributes and the
observed MotionControl mode. Addresses retain their integer kind. Only after this
admission does comparison use project-relative hardware/PLC paths and channel
selectors/settings. Original owner contexts and provider IDs remain in evidence.
Additional channel settings and connection values still participate in comparison.

Qualification captures this evidence before/after the edit, after recovery and on
both reopens. The baseline endpoint must match the seeder's exact authorized
module. Current source admission binds every summary to original inventory,
technology and hardware calls in order, without an intervening mutation. The
read-only companion and independent auditor use the same production admission;
summary/request/result tampering fails. The audit reports
`endpointCorrespondenceVerified` separately. Legacy diagnostic reports do not gain
this flag or permission to run the current companion.

## Verification

All 224 general managed checks pass, including escaped path membership and
foreign/duplicate/cyclic hardware refusal. All 58 encoder Python test methods pass:
the lifecycle fake now emits real connection/reference/inspection structures and
exercises production validators, with no empty connection mock. Recovered paths
and provider IDs intentionally differ. Negative tests cover foreign owners,
missing or ambiguous members/channels, wrong metadata/attributes, boolean integer
substitution, changed addresses and original-call tampering. Tests use synthetic
objects and processes; they do not execute Siemens APIs.

The first focused run failed because one refusal uses the existing RuntimeError
from the inventory validator rather than AssertionError. The assertion now accepts
both established refusal types. The failed log is retained at
`artifacts/encoder-endpoints-focused-01.log`.

Final frozen Git tree: `57df9fbbe1f24dde68ae8e8155e142ad266fc711` (2030 files).
Source manifest (`artifacts/encoder-endpoints-01/source-manifest.json`) SHA-256:
`e11d7e72585f9e95ba73728c4b6464d54f70b80de6e26e36f6b5c28ffbd34db3`.
Source ZIP SHA-256: `b8cd53b1aaeb3fa8b65ea614c0640d1d6dcc40946599cffd0618c55792c4099b`.

The aggregate (`artifacts/encoder-endpoints-01/source/artifacts/checks/result.json`)
passes 41 managed suites, 1,420 Python tests (one unavailable-evidence skip),
24 Explorer model/client checks, contracts/source bindings and all offline MCP
workflows. The general executable passes 224 checks. All 58 encoder Python
methods also pass under -OO, including 33 lifecycle and 5 endpoint methods.
Run: 2026-10-17T13:59:02.3225544Z to 2026-10-17T14:10:22.0973261Z.
Aggregate receipt SHA-256: `32978e4950379c04806bd2aa72e261d62d9826b04ed34ff298e63af0b72b1d21`.
Console evidence (`artifacts/encoder-endpoints-01/aggregate-console.log`).

All 24 package stages (`artifacts/encoder-endpoints-01/source/artifacts/package-checks/result.json`)
pass, including exact ZIP/manifest verification and relocated MCP workflows.
Bundle `tia-mcp-offline_20261017_135924_a6a42869` contains 1434 manifest files.
Package receipt SHA-256: `4d0cacea063c6df106c5238535f185a86d60d1346877d0f6b55947036026086c`.
Manifest SHA-256: `0d75ab55b14885a55caefe46ccf6ce6edea6e6d07cf3bb6614551079809a62e3`.
ZIP SHA-256: `b4ea358d3029b3e2ddd457ff04b1fab65bc7926c3eff3d0fe47ab16701abf45e`.
The portable package disables native workers. Browser tests were not repeated
for this native-reference/qualification-harness repair.

The source comparison (`artifacts/encoder-endpoints-01/final-source-comparison.json`)
verifies unchanged frozen source. The final commit changes five completion
documents and removes one trailing blank line from `encoder_endpoints.py`; the
comparison enforces that exact formatting-only source difference. All 106 unavailable historical evidence binding rows
remain unchanged. The integration receipt (`artifacts/encoder-endpoints-01/integration.json`)
records the pushed commit, exact hosted CI outcome and cleanup.

## Remaining work and limits

Closed saved-copy byte admission is still missing from the encoder companion;
its path restrictions and original terminal evidence do not prove the retained
project files stayed unchanged before a later open. The retained synthetic reproduction (`artifacts/encoder-endpoints-01/next-admission-reproduction.json`)
changes project-file bytes before companion opening; current admission still
dispatches the open and the synthetic audit passes. This proves a missing pre-open
file gate, not successful native recovery. This is the next offline repair. Complete unrelated project-content preservation also remains open.
These observations are not atomic or authenticated native evidence. Siemens V21
SDK compilation, native object equality/provider behavior, real archive recovery,
persistence and physical/runtime acceptance remain unexecuted. The full D13/D14
and 44-workstream goal remains active. No public tool, mutation kind or feature
count changes in this increment; all 106 unavailable historical bindings remain.
