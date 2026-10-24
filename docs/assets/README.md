# README visuals

The visual identity uses oversized black headings, square borders, a warm paper
background (`#f2f0e8`) and acid yellow (`#dfff00`). Keep data tables readable and
use the accent for navigation and selection. Warning and error states retain
their own colors and text labels.

- [Social card](tia-mcp-social.png): 1280 × 640 PNG, with an editable
  [SVG source](tia-mcp-social.svg). Ready for a repository social-preview upload;
  committing this file does not change the repository's hosting settings.
- [Project icon](tia-mcp-icon.png): 256 × 256 PNG and
  [SVG source](tia-mcp-icon.svg). The connector mark also appears in the Explorer
  header. It can be reused wherever a project avatar
  is supported; it does not replace the owner's account avatar.
- [Architecture](architecture.svg): component boundaries, with a
  [text explanation](../guides/architecture.md).
- [Engineering workflow](engineering-workflow.svg): prepared changes,
  checkpointing and explicit recovery. The [guide](../guides/engineering-workflows.md)
  defines the exact operation requirements.
- [Capability overview](capabilities.svg): requirements and evidence categories,
  not a live status dashboard. Use [Current status](../STATUS.md) for updates.
- `tia-mcp-banner.svg`: original brutalist industrial artwork; editable vector,
  1280 × 400. All lettering and schematic shapes are self-contained, with no
  external fonts, scripts, embedded images or Siemens logos.
- `project-explorer.png`: actual Project Explorer browser capture at 1440 × 1120,
  captured on 2026-10-24. Uses the unchanged synthetic packaging-line snapshot
  from `ui/project-explorer/tests/fixture.cjs`, rendered with the existing
  `ui/project-explorer/pack.mjs` bundler and Explorer assets.

To reproduce the screenshot, package that fixture with `pack(snapshot, outputPath)`,
open the generated HTML in a browser with the light theme, search for
`MotorControl`, select the result in `PLC_A`, and open **Relationships**. Capture
the full viewport at the dimensions above. Keep the offline notice and fixture
name visible; do not substitute private project data.

The screenshot demonstrates the viewer with sample data. It does not demonstrate
a live TIA Portal session, native capture, or runtime qualification.

The Explorer's light and dark themes use the same visual language. Its exported
relationship and structured-network diagrams use the light paper palette so they
remain self-contained when shared. Dense engineering values retain the existing
layout and readable text sizes. Keyboard focus uses a distinct blue outline, and
loading animations stop when reduced motion is requested.
