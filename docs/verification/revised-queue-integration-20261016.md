# Revised parallel queue integration — 16 October 2026

This is the current integration record for the user-confirmed four-slot queue.
The earlier completion audit and recheck are retained as historical inputs.
The user requested a status checkpoint, commit and push after the current watch
trial completed. Further native qualification is stopped at that checkpoint;
unfinished admission work remains explicit below. No installed package has been
changed and no production profile has been activated.

| Slot | Current delivery | Remaining gate |
| --- | --- | --- |
| Integration | Catalog, obligation mappings, suite registration, maintained test utilities and dependency declarations repaired. Private clean solution build has zero warnings/errors; browser checks pass. Source bindings validate 155 tool declarations, 55 mutations and 228 features. | Final checkpoint validation is recorded below; this is a development checkpoint, not release acceptance. |
| OPC UA | Eight offline MCP tools; 24 managed workflow, six schema and 48 actual stdio checks pass. Isolated parser/CLI checks pass. | Final combined checkpoint identity; native engineering remains outside this offline delivery. |
| Translation | Exact installation-owned admission architecture and six normal tools implemented; 65 admission/registry checks and 41 actual Host boundary checks pass. Pre-dispatch interruption and import-reply loss trials pass with complete recovery and cleanup. | Same-binary, same-instance remaining failure-case evidence, registry review and normal-Host admission proof. No profile is installed. Package04 and its native trial were not started. |
| Watch authoring | Finite one-row Bool profile predicate and offline negative checks implemented. Corrected package03 passed real saved source drift/refusal/restoration, creation, three collision refusals, stale-epoch refusal, six-peer preservation, saved fresh reopen and distinct recovery to absence with fresh recovered reopen. | Creation-specific lost-reply native trial and exact production admission/default-Host proof. The loss controller/oracle have eight passing portable checks; proposed packet03 was not dispatched. Production creation remains gated. |

Validation is partitioned by prerequisites in [validation runners](../reference/validation-runners.md).
Portable checks include real Git, separate-process persistence, response schemas,
and actual stdio MCP with absent native workers. Workstation boundary tests and
retained native fixtures have explicit separate runners. No omitted suite is
silently counted as passed.

The first clean checkpoint is `D:/TIAW/iq161/source`, with 1,728 files and adjacent
manifest SHA-256 `dc4b7fb810c8defa854b727c39b7f5c5b56cd8f0f9723279c10d40303d0ec820`.
Its 27-suite managed partition, translation/Test Suite/OPC UA actual MCP checks,
Test Suite schema harness, catalog checks and real Git/process tests passed.
The retained fixture runner passed 24 translation checks and the watch reindex
checks, while installed Siemens GRAPH schema validation passed 51/51.
It predates the later admission and lifetime fixes and therefore is not the final
delivery identity. Its first aggregate stopped because ignored historical native
artifacts were missing from the source-only snapshot. Explicit portable binding
validation now reports missing evidence while preserving strict source/anchor and
available-artifact comparisons. The failed receipt is retained.

The isolated OPC UA runner passed all checks but initially propagated the expected
negative overwrite-test exit code. The runner now resets the exit code only after
all checks succeed. The worker IL audit caught Test Suite scope shutdown accessing
a raw process descriptor outside its original boundary. A separate parameterless
owner now creates its own UI Portal, retains it in a readonly field, and closes
only that instance. The IL audit rejects injected, attached or mutable ownership;
144 general checks and the current compiled-worker audit pass. This structural
repair does not qualify Test Suite execution or UI behavior.

Native windows remain serialized by integration and bound to reviewed immutable
packages. Translation `ta1601` and `ta1602` each closed three exact owned lifetimes
and zero owned reservations. Watch `we1601` is a retained failed attempt. Its separate read-only
reconciliation proved all six peer exports and the source binding unchanged,
three fresh normal process exits, and zero pending owned reservations. Older
unrelated reservations are neither adopted nor cleared. No PLCSIM or UI workflow
has been run for this queue.

Watch `we1602` passed on frozen package03, source manifest SHA-256
`cf6ddbf03e92cc0a1694f521bbbd0d48e96da04e4295af928c23561e4545a666`
and worker SHA-256
`f465aa19436d10ef42bf43c8314d4ee62d37c1d50b9d24057e63359fcc056157`.
Its terminal audit is
`artifacts/watch-exact-20261016/native-terminal-02.json`, SHA-256
`acc6812c2f608d648360f569d9f6ceb8b85c3c4017e7d4aa2fc2eed4fc8006ee`:
eight retained exact process handles exited with code 0, zero owned resources
remain pending, and all 49 foreign-store locks plus owned-store locks closed.
The first failed `we1601` attempt and its separate unchanged-baseline reconciliation
remain retained. These exact package receipts do not qualify a later rebuilt worker.

Detailed lane records: [OPC UA](opc-ua-host-delivery-20261016.md),
[translation admission](translation-admission-20261016.md), and
[watch exact authoring](wt06-exact-authoring-20261016.md).

The repository is hosted privately at
[Sparkz400/TIA_MCP](https://github.com/Sparkz400/TIA_MCP). The user intends public
visibility at release time; this checkpoint neither changes that visibility nor
constitutes a release. Generated local build/native artifacts remain outside Git
under the existing ignore rules; retained source fixtures and status records are
included.

Final checkpoint validation uses the staged Git checkout at
`D:/TIAW/iq163/source`, 1,740 files, manifest SHA-256
`93814f061104cc75cc7a9ccd93fc039d9d5f6cf844dd5e7e72636929de85bf8c`.
Its first aggregate exposed two retained documentation hashes whose CRLF bytes
changed under the repository's LF policy. The text inputs and binding receipt
were refreshed; strict local validation and clean-checkout validation then passed
for 155 declarations, 55 mutations and 228 features. The portable check explicitly
reports 106 absent historical artifact inputs and never promotes their evidence.
Byte-sensitive fixture paths, including test-project fixtures, preserve their
original bytes. The initial failed aggregate is retained.

The final solution Release build passes with zero warnings/errors. The staged
checkout passes 1,237 Python tests with one expected skip for an unavailable
task-local native observation, 21 explorer model/client checks, the 31 watch,
12 component and 96 Test Suite worker boundary checks, and the compiled-worker
lifetime guard. No Siemens/native process is started by these checks. The earlier
24 browser checks, installed GRAPH XSD checks, isolated OPC UA runner, and explicit
retained-fixture checks remain separately identified above; no hosted CI outcome
is claimed by this local record.

At the user's request to finish immediately, the final 27-suite managed rerun
was still in progress when this commit was prepared. Its receipt belongs at
`D:/TIAW/iq163/source/artifacts/final-managed-01/result.json`; it is not counted
as passed by this checkpoint. The earlier complete 27-suite pass is retained
above. All implementation and test blobs in the commit match the staged source
used by the final checks; only documentation and the corrected binding receipt
changed afterward.

Broader feasible 1.0 obligations in the [release ledger](../plans/release-obligations-20261015.md)
remain open. This queue does not promote historical fixtures to broad native,
runtime, hardware or release qualification.
