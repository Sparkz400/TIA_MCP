# Named local VCI branches — 16 October 2026

Scope: D01 named initial/private child branches, descendant commits, branch
inspection with pinned pagination, retained fork receipts and exact revision
materialization. All work uses actual local Git and the .NET Host with no Siemens
products. Parent revision repositories remain unchanged.

The preceding GRAPH/CI repair commit `e1a4a2d` passes all hosted checks: Python,
managed and isolated OPC UA in run `38904606338`, Explorer in `38904606691`.

Development evidence:

- Git runner: 122 checks passed (84 pure and 38 actual Git/process groups),
  `artifacts/vci-branch-dev/runner01.log`. New groups cover named branch byte/ref
  preservation, a competing branch creation, missing completion/replay refusal,
  and invalid branch object types. Further case/path collision assertions are
  included in the final aggregate. Native journal admission deliberately does
  not include the new offline-only fork operation.
- Workflow: `workflow02.log` passes 77 checks, including full raw-file and package
  workflows. Extra legacy-review and duplicate-artifact regression assertions
  are included in the final aggregate.
- Actual stdio Host: `mcp02/verification.json` passes 72 checks with seven Host
  lifetimes. Every request/result follows the updated JSON Schema. Checks cover
  branch review, commit, actual restart, pinned pages, unchanged parent refs,
  exact raw bytes, retained callbacks, inherited branch, collision refusal,
  changed-page refusal, materialization and unknown interrupted preparation.
- The first workflow/MCP attempts exposed the old two-artifact assumption.
  Retained verification now requires the exact expected names for reviews,
  exports, mappings and comparisons, including all fork callback artifacts.
  Failed attempts remain at `workflow01.log` and `mcp01/verification.json`.

The first staged aggregate (`artifacts/vci-branch-01`) passed contracts, bindings
and retained-evidence checks, then stopped at an outdated independent tool-set
assertion in `test_vci_contracts.py`; managed execution did not start. The test
now includes `vci_branches` and independently checks pinned continuation inputs
and rejection of native qualification. Its receipt is preserved. The isolated
OPC UA runner from that same source tree passed.

## Final staged-source validation

The full portable aggregate passes from `artifacts/vci-branch-02/source`:
29 managed suites, 1,258 Python tests (one unavailable-native-evidence skip),
24 Explorer model/client checks and all actual stdio workflows. This includes
122 Git checks, 80 VCI workflow checks, 72 VCI MCP checks and 43 GRAPH MCP checks.
The final branch assertions cover case/path collisions, duplicate artifact
inventories and reading/committing older reviews without branch provenance.

Tested Git tree: `18f8c4b66f127fb6e54e27255b8e1b2de7817a2e`.
Source manifest SHA-256:
`83ade40386f665d7db512f3d133fdfc050856682f4691985956a2731ba703db8`.
Aggregate result SHA-256:
`5e5a9db77148e40bb34d684eaa4399420df7db37226fd55d74c1e1027230515d`.
VCI MCP receipt SHA-256:
`d8ac717e51016f8d0ee08cdc8a5dd9dc138cf0ef5a3790b59994ca16f46e0f86`.

The isolated OPC UA pass above used identical application sources; the later
snapshot adds the independent Python contract regression. No browser code changed
in this increment; the previous browser/hosted Explorer evidence remains scoped
to that unchanged code. Only documentation changed after the final tested tree.
Published commit `95052ac6cbc37fc515f64fbf07c99578df4cf797` passes all hosted checks: Python, managed and isolated OPC UA in `38908492475`, Explorer in `38908492690`.
Standalone ref reset/delete/rename/merge, remotes, native VCI, engineering
compilation, runtime and release acceptance remain open.
