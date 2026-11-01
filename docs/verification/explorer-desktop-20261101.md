# Explorer desktop roundtrip — 2026-11-01

VS Code 1.144.0 passed the [desktop MCP Apps host acceptance](../guides/project-explorer.md#desktop-mcp-apps-host-acceptance-manual)
with the production Host (`--toolsets core,explorer`) and a fresh native capture of an owned scratch project.
Evidence: `local.explorer.desktop.20261101`. The 25 October record covered rendering over retained data only
([record](explorer-desktop-20261025.md)).

## What passed

[`qualify_explorer_desktop.py`](../../scripts/qualify_explorer_desktop.py) `serve` relayed every message unchanged
into a transcript; `verify` checked it and ran its own clients:

1. **Negotiation and resource read.** The Copilot agent negotiated MCP Apps through the MCP 2026-07-28
   `server/discover` handshake (capabilities in each request's `_meta`), listed `project_model_capture` with
   `_meta.ui.resourceUri` and read `ui://tia-project-explorer/view.html`. A client without MCP Apps saw no app UI.
2. **Exact selection.** `TiaMcp_EnableGate` was selected in the view and sent; the view's `project_model` `verify` call confirmed the
   six-field reference, and the next `project_model_details` call carried it unchanged. An independent `block_read`
   in a fresh owned TIA returned the same block identity.
3. **Refusal after detach.** After `tia_detach` the reference was refused with `SessionMismatch`; the view showed
   `ModelInvalidated` for the detached snapshot.
4. **Artifact fallback.** The client without MCP Apps captured the same project and exported HTML that references
   no network resource.

The operator confirmed the rendered view, no network request from the view in the developer tools and the stale
state; four screenshots are kept with the run.

## Runner changes

- The transcript gate recognised only `initialize`; it now also reads the `server/discover` handshake.
- `ModelInvalidated`, the store's code for a snapshot that `tia_detach` invalidated, joined the accepted refusal codes.

## Limits

- One product on one Windows desk.
- VS Code reloads the view when it scrolls back into sight and drops the selection, so after the detach the
  reference was reused from chat (`project_model_details`), not from the view's button.
- SSH/VM transport stays open (B07).
