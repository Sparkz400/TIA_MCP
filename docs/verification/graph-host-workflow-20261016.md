# GRAPH Host and Explorer validation — 16 October 2026

Scope: X06's supplied-document Host/Explorer delivery. No Siemens software, SDK,
native worker or runtime was used. The standalone document reader remains the
existing implementation; this increment integrates normal MCP tools, durable
artifacts, exact-selection Explorer attachment and validation contracts.

Development checks passed:

- Host build: `artifacts/graph-host-dev/build`, zero warnings/errors.
- Managed workflow: 25 checks, `artifacts/graph-host-dev/run01.log`. Exact source
  bytes/receipts, restart, paging, structural comparison, invalid requests, external
  entity refusal, over-limit XML, escaped HTML, explicit oversized-row omissions,
  comparison-chain tampering and incomplete publication discovery.
- Actual stdio Host: 43 checks and three Host lifetimes,
  `artifacts/graph-host-dev/mcp02/verification.json`. Every GRAPH request/result is
  JSON Schema checked. Native paths deliberately do not exist; shutdown receipts
  establish no native worker started. The first attempt failed because the test
  incorrectly treated an empty initialized native store as a native operation;
  its receipt remains at `mcp01` and the assertion now checks for actual entries.
- Explorer model/client: 24 checks, including chart/sequence identity scope,
  duplicate/missing endpoints, source tampering and immutable exact attachment.
- Browser: 29 checks in each of `artifacts/graph-host-dev/browser01` and `browser02`.
  The second consumes `mcp02/inspection.json` produced by the real Host. Checks
  cover sequence/item navigation, source download, snapshot export/reopen,
  same-name isolation, stale chooser rejection, escaping and existing MCP transport
  behavior. These are Chromium/fixture tests, not a real desktop MCP host session.

## Final validation

The full aggregate passed from staged Git tree
`587d6d97568cd2e5c5c2a9c6ea3d285bd6737cd1` in
`artifacts/graph-host-01/source`. The source manifest SHA-256 is
`11dc4942664c71f7cbc44900b30c19948dcbefb1ea7ef8785186f760082d84ba`.
The runner completed 12:46:33–12:53:38 UTC: **29 managed suites, 1,257 Python tests
with one unavailable-native-evidence skip, 24 Explorer model/client checks and all
actual stdio workflows**. These include 25 managed GRAPH workflow checks and
43 GRAPH stdio checks, 64 managed VCI workflow checks and 54 VCI stdio checks.
Contracts/bindings cover 172 declared tools, 55 mutation kinds and 245 features;
normal Host registration excludes separately gated qualification tools.
Retained evidence remains six matched files and 106 unavailable files.

`artifacts/graph-host-01/source/artifacts/checks/result.json` SHA-256:
`bba91d1bc1643bc713eaff8051c79d007c0be77e02a0a9553c7ad8b9f9b305f3`.
Its GRAPH stdio receipt SHA-256:
`9546c4fe129757f02ade5607a3199b0517e3e268922661bad1d506f416646557`.

Visual review then corrected the reused SVG caption from native connections to
**document connections** for GRAPH. Eight previously garbled punctuation sequences
in the documentation index were also corrected. The final source snapshot is
`artifacts/graph-host-02/source`, staged tree
`bd41fb4aea386f65ffbe565636ecf463c71e45f6`, manifest SHA-256
`e6f623a08f5f72be18b53bd4cd58043be4ec4b23e577be1a16d9ae29aee5e711`.
Its only differences from the full-run tree are the caption, its regression
assertion, regenerated portable HTML/manifest and documentation index punctuation.
Managed implementation, tool contracts, bindings and MCP harness are unchanged.

That final tree passed a fresh zero-warning/error Release Host build, 24
model/client checks, 43 actual GRAPH MCP checks and 29 browser checks using its
own Host-generated report. The browser recorded no page errors or network
requests. Measured 1k/10k/50k tree first viewports were 14.5/64.9/269.3 ms;
500-node schematic graph renders were about 47–49 ms. These measurements are
local fixture observations, not target-machine or maximum GRAPH-size acceptance.
Screenshots are retained under `artifacts/graph-host-02/screenshots`; the panel
was visually inspected during development. Both portable bundle source assets
and complete HTML are hash-bound by the generated manifest.

Final GRAPH MCP receipt SHA-256:
`c0b84e7f66379613acf52f0bbc1413f98fa6f71f4033ddf6a1a3894d9abfda76`.
Final browser receipt SHA-256:
`09cac3a51ecf5e874f989fcbc1072e5ec778eeeecd673f3aaedd3b7133dca349`.
The application sources above are unchanged after these tested trees. A separate
CI snapshot repair and documentation updates follow, as recorded below.

## Hosted CI dependency repair

Published application commit `5afb438f97adc6846c014f02f775e1ccd6f3fd08` passes
hosted Explorer run `38903934244` and the Python job in run `38903934552`.
The isolated OPC UA job failed: its partial source snapshot copied Core but
omitted Core's new GRAPH project dependency (`MSB9008`, then `CS0234`). The full
aggregate already included that dependency; it did not expose this partial-copy
error. The hosted failure artifact remains at
`artifacts/graph-host-02/hosted-opcua-failure/e75b618b914b46d39f8f96ad3b0ecad5`.

The repair adds `src/TiaMcp.Graph` to the isolated runner's explicit source list.
No application or tool contract changes. The actual isolated runner now passes
from `artifacts/opcua-offline/6d1c0e84285243e0bf217d31a7817054`: nine metadata tests,
31 parser checks, 27 dependency checks, CLI impact expectations and immutable
output refusal. Native acceptance remains `notRun`. Source manifest SHA-256:
`31671a1e0c83eefed9335ed10f900797b0eedb3afcda886eafdd031af34146bf`;
result SHA-256:
`9195d44f932d1a5291f4dbe501393f4c342e8e3e2068306d093cae827cbf73b9`.
Repair commit `e1a4a2dbb844b32b500c20755e94cfdddb40bc09` passes hosted Python, managed and isolated OPC UA run `38904606338` and Explorer run `38904606691`.

No native source association, schema acceptance, rendering, engineering operation,
hardware/runtime behavior or release readiness is claimed. X06 CFC/DCC research,
the remaining offline queue and all native gates remain open.
