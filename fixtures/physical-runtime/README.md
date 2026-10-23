# Runtime fixtures

All files here are supplied or loopback test evidence. None qualifies a Siemens controller, firmware, engineering installation or running program.

`association-review.json` and `association-request.json` were emitted by `TiaMcp.Diagnostics.Tests` from the independently authored `fixtures/trace-diagnostics` XML and mapping oracle. They retain the synthetic fixture markers, exact source paths and nested `Conv2` instance. The case is created anew through `diagnostic_plan` in the runtime MCP harness; it is not a hand-written successful response.

`capture-associated.json` is an actual export from `artifacts/runtime-association-01/mcp-02` on 18 October 2026 (local time). The Host mapped two loopback Bool/UInt selections to that diagnostic case and retained both source declarations. All address, program, source-authenticity and native qualification flags remain false. The exact case/review were removed before export to test self-contained retention. The dynamic loopback origin and IDs are observations, not endpoints to connect to.

`capture-portable.json` is the earlier unassociated loopback export. It remains unchanged to exercise reading older capture records without association fields. `session-v1-closed.json` is the retained earlier-build session compatibility record.

## Capture analysis fixture

`capture-analysis.json` is an actual `runtime_capture_analyze` report from the disposable loopback MCP harness on 18 October 2026. Its case adds an explicit Bool/UInt pattern to the authored conveyor request. It contains two sequential cycles, nine uncaptured case signals and unresolved verdicts. The endpoint, certificate, IDs and timestamps belong to that synthetic execution. It is not native evidence or a verified program/address association.

## Alarm binding fixtures

`alarm-review.json` and `alarm-request.json` were emitted by the diagnostic test
fixture on 18 October 2026 from authored conveyor XML and explicitly synthetic
normalized alarm operands. The Host workflow creates a fresh diagnostic case
from these original bytes. `capture-alarm.json` is an actual managed
RuntimeCaptureStore export from RuntimeAlarmBindingChecks, with one deliberately
failed read, one selected signal and three retained bindings. Its review was
generated separately and may have a different hash from alarm-review.json.
It exercises offline viewing and retention, not a real endpoint or active alarm.
Existing historical fixtures are unchanged.
