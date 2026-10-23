# VCI package Host workflow — 16 October 2026

This increment exposes the existing `VciManifest` and `VciComparison` models through
two normal Host tools, connected to the retained local Git workflow. It adds no
Siemens dependency and does not invent a native adapter or native observation.
The [caller guide](../reference/vci-package-workflow.md) describes inputs and limits.

## Focused evidence

- `artifacts/vci-package-dev/run01.log`: 62 actual-Git workflow checks pass,
  including the first package capture/comparison cases. This precedes the final
  explicit completeness/version boundary.
- `artifacts/vci-package-dev/run02.log`: 64 actual-Git checks pass. New cases cover
  raw divergence despite Equal status assertions, missing companions, incomplete
  enumeration, stale hashes, ownership and exact-identity conflicts, upload release,
  restart, input-chain tampering, report corruption, strict metadata and bounds.
- `artifacts/vci-package-dev/mcp01/verification.json`: 54 actual stdio checks pass
  with native worker paths absent. Mapping upload/release, package capture, three
  independent revisions, Host restart, complete report downloads, independent file
  hashes and JSON schemas, stale review and input-chain tampering are exercised.
- Six Python schema/contract groups pass, covering both new tool registrations,
  D01 mappings, manifest-kind admission, explicit hashes and false native flags.

An initial fresh build was invoked with `--no-restore` and lacked its assets file;
the normal isolated build restored existing dependencies and passed without warnings.
No new dependency was introduced. The final aggregate below passes.

## Hosted CI finding and repair

The preceding commit `b51f3da` passed hosted managed, isolated OPC UA and
Explorer checks.
Its Python job
failed before tests: three generated contract JSON inputs contained CRLF locally,
whereas `.gitattributes` publishes/checks them out with LF. The locally generated
binding hashes consequently did not describe the GitHub checkout.
The job log is retained as `artifacts/vci-package-dev/previous-ci-python.log`.

The edited source contracts now use LF. The binding collector rejects CRLF source
inputs before creating a new binding, and a regression test verifies refusal plus
unchanged CRLF evidence bytes. All 30 binding tests pass. Historical evidence is
not normalized or reinterpreted, and the 106 unavailable receipts stay unavailable.
The final aggregate below validates the repair from the staged Git tree.

## Final combined evidence

`artifacts/vci-package-01/source` was extracted from staged Git tree
`38441bbfa455859fc99cd9beecb53af23b0d7bdf`, so its source bytes are the actual Git
publication representation rather than the Windows working-tree representation.
Its source manifest is `artifacts/vci-package-01/source-manifest.json`, SHA-256
`d610b5565d229b7e544cc94b67639e644a05e9643234494202dd7852d905a89c`.
The archive and tree identity remain retained beside the source snapshot.

The complete aggregate passed from `2026-10-16T12:14:39Z` to
`2026-10-16T12:21:39Z`: 28 managed suites, 1,257 Python tests with one
unavailable-native-observation skip, 21 Explorer model/client checks, source
bindings, evidence inventory and all actual stdio workflows. VCI passed 64
managed workflow checks and 54 stdio checks across six Host lifetimes. Contract
coverage is 166 declared tools / 55 mutation kinds / 239 features; the normal Host
registers 146 tools. Native qualification remains false.

The intentional Host interruption again retained the original Host process handle
and verified its exit. The process-tree utility returned 255 with an unsuccessful
child-stop report. That child PID was absent in a subsequent process inventory;
this is not retained-handle proof for every transient Git descendant. No native
worker started. This limitation remains explicit rather than treating utility
output or a later process snapshot as stronger cleanup evidence.

Only documentation changed after this tested Git tree. Hosted checks for the
following publication remain pending; the preceding failed Python job is retained.

## Unexecuted gates

No Siemens SDK compilation, native mapping/export/import/synchronization,
companion/language round trip, engineering compile/reopen/checkpoint recovery,
PLC/runtime behavior or native admission was executed. Supplied mapping assertions
are not authenticated native evidence. Upper storage bounds are safeguards, not
measured maximum-size performance acceptance. The broader D01 and 44-workstream
obligations remain active.
