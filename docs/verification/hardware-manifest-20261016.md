# Hardware preservation manifest identity - 16 October 2026

The D08 source audit reproduced a false preservation pass in
`HardwareEngineeringRules.RequirePreserved`: sorting with `OfType<JObject>()`
removed malformed top-level rows from comparison. A move could therefore be
reported preserved even when an additional non-object row existed after it, or
such a row disappeared from the baseline. Duplicate or missing unrelated paths
could also be accepted as unchanged identities.

The shared comparator now validates both manifests before selecting or sorting:
every row must be an object with a unique nonempty, control-free string path.
It then sorts every row and keeps the existing strict field comparison. Missing
selected rows return `MutationVerificationFailed` rather than an unstructured
`Single` exception. No native mutation, admission or recovery policy was weakened.

Callers: existing hardware post-write verification for plug, move, channel,
address and distributed-I/O assignment. Component/watch captures and exact
recovery comparisons remain unchanged. No new Host tools, schema, dependencies
or mutation kinds were added.

Development evidence under `artifacts/hardware-manifest-dev`:

- `before.log` reproduces acceptance of an added malformed row.
- `after-02.log` passes 65 linked-source checks, including 19 new cases for
  malformed/duplicate/missing identities, structured absence refusals, case and
  order preservation. The retained V21 initial-server fixture still passes, and
  its undeclared-companion negative control still refuses.

## Frozen validation

Source: `artifacts/hardware-manifest-01/source`, Git tree
`00205268cb8f4202fddf0cc39b0de024915fcef8`, 1,846 files, based on
`5906921fbf30bfc2659a1d24591fa6fadd77d5cd`. All recorded source hashes match;
working implementation, contracts and tests match the frozen source. Subsequent
changes only update this record, STATUS and the offline queue.

`Test-Offline.ps1 -OutputDirectory artifacts/checks` passes the complete aggregate:

- 31 managed suites, including all 65 hardware preservation checks.
- 1,280 Python tests, with one unavailable-historical-evidence skip.
- 24 Explorer model/client checks and every actual offline MCP workflow.
- Contract/source bindings, 258 catalog features and 165 normal Host tools.
- Retained evidence: six byte matches and 106 unavailable historical files;
  missing evidence is not reconstructed or promoted to a pass.

| Receipt relative to `artifacts/hardware-manifest-01` | SHA-256 |
| --- | --- |
| `source-manifest.json` | `dcdde546016c8aea0d2776709895925df6f7a6530e31e9d42b9122237b165394` |
| `source/artifacts/checks/result.json` | `efe059897103659e129f911ebc3df52ebde95fa56d90c0ca72f276a335de67e0` |
| `aggregate-console.log` | `7fd7b19f98924fd90bd60ec6545c8c580b733c6afaf6799d242ae2845f5b878d` |
| `source/artifacts/checks/catalog-mcp.json` | `ebc754197c6cca65f60908636b22958a966e2aa347b4ea2b82655349a91ca662` |
| `source/artifacts/checks/retained-evidence.json` | `c431749371fe7d798fa2d00532165928c4bf1bdcf39a6d5e738f4fc35dbc7321` |

The package/relocation implementation is unchanged and was not rerun locally for
this increment. The comparator belongs to the native worker, which is excluded
from the offline bundle. The previous software-unit commit `5906921` passes all
hosted jobs: Explorer `38965378891` and Python/managed/package/OPC UA
`38965378910`. This increment's hosted runs must be read separately.

## Native and remaining limits

This workstation cannot compile the Siemens-dependent worker: no installed SDK
exists, and Siemens' NuGet reference package only resolves assemblies from an
installed product. Running this exact pure source in the portable test suite is
not a native worker build or qualification. The catalog preserves historical
profile evidence and explicitly records the new guard's portable-only validation.
Native build, fresh readback/reopen, recovery and runtime acceptance remain open.

The accompanying [D08 plan](../plans/expansion-plans/communication-connections.md)
records supported documentation, remaining accessor/resource gaps and full
PLC-to-PLC/PLC-to-HMI workflows. General connection lifecycle remains open.
