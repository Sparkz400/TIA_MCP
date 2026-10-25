# Explorer desktop acceptance — 2026-10-25

VS Code 1.140.0 rendered the real Explorer HTML through MCP Apps using a
local, read-only retained-snapshot fixture. This exposed a missing
`ui/notifications/size-changed` notification: the host initially allocated a
300-pixel-high panel, leaving the Explorer controls below its visible area.
The client now observes its content size, reports changed dimensions to the
negotiated parent, and stops the observer and pending notification on teardown.
The notification follows the [MCP Apps sizing contract](https://github.com/modelcontextprotocol/ext-apps/blob/main/specification/2026-01-26/apps.mdx#container-dimensions).

## Observed desktop behavior

- The installed VS Code product advertised `io.modelcontextprotocol/ui` and
  `text/html;profile=mcp-app`; it listed the tool and read
  `ui://tia-project-explorer/view.html`.
- The fixed view grew to expose the project tree. Reloading the isolated
  workspace and calling the fixture again established a new connection.
- Searching `TiaMcp_EnableGate` returned the exact function block and its
  instance DB. Details retained the function block's full object path.
- The Relationships tab displayed retained native evidence, including
  unresolved endpoints and the explicit incomplete-coverage statement.
- Down/Enter selected `TiaMcp_EnableGate_DB`; the subsequent relationship
  request used that DB's exact reference, rather than the previous block.
- **Send selection** requested verification and showed `stale selection`.
  The fixture deliberately refuses current-session verification; no successful
  native selection handoff is claimed.

The input is the unchanged 2026-10-24 native scratch export
`artifacts/q24/explorer.snapshot.json`: snapshot
`4f5a45b25df64631b63350dbacb22db0`, 73 nodes and 15 edges. Its native capture
and expiry times were preserved. The fixture uses the existing offline query
implementation and has no engineering worker or mutation route. This is
desktop UI evidence over retained data, not a current native capture.

## Regression and package checks

- 26 model/client tests passed, including changed-size notification,
  duplicate suppression, pinned origin and observer teardown.
- 45 browser checks passed in Chromium 156.0.8078.4, with no page errors or
  network requests. The new compact-host case starts at 300 pixels, grows to
  fit content, and exercises search and selection without horizontal overflow.
  Existing checks cover delayed responses, lost sessions, keyboard navigation,
  relationship direction, hostile content and verified artifact downloads.
- The offline package is independently built and checked with the existing
  package and stdio-host acceptance scripts. Exact input/output manifests,
  package hashes and final results are retained with the local evidence.

Evidence is preserved under `artifacts/explorer-acceptance-20261025/`: desktop
before/after screenshots, accessibility state, fixture source and MCP trace,
browser results, and package/host reports. The desktop product's network
traffic was not independently recorded; the no-network result above belongs
to the instrumented browser suite.

## Still open

The [full desktop/native checklist](../guides/project-explorer.md#desktop-mcp-apps-host-acceptance-manual-open)
still requires the production engineering Host, fresh capture, independent
`block_read`, successful exact selection roundtrip, and refusal after native
detach. The retained-data fixture cannot close those checks. No complete
native provider coverage, new native scale qualification, physical runtime
qualification or release approval follows from this record. Claude's native
test queue was left untouched.
