# Independent supplied HMI output effect verification

17 October 2026. Native execution: **not performed**. Scope is the portable
component-review v3 request, immutable record, paged effects and public Host lifecycle.
The [workflow](../reference/hmi-component-review.md#independent-supplied-output-effects-v3)
defines the exact evidence limits. No native mutation or qualification gate is enabled.

The existing C01 proposal review had no independent post-operation file set.
V3 compares supplied observed files with proposed files for create/update, current
files for retain and an empty set for remove. It detects missing, unexpected and
changed files, keeps unknown coverage distinct and preserves all originals.
This adds an observation oracle at the file level. No XML/YAML normalization,
native object association, generated ownership or complete project coverage is inferred.

Targeted managed checks: **166 passed**. Public MCP: **204 checks passed**, with two
clean Host lifetimes, upload release/restart, strict v3 schema validation, paging,
legacy section refusal, exact original downloads and forged-effect rejection.
Evidence: `artifacts/hmi-effects-targeted-01/verification.json` and
`artifacts/hmi-effects-mcp-targeted-02/verification.json`.

Initial local invocations exposed two setup errors: a no-restore build lacked its
assets file, and the first MCP command used `net10.0` instead of the actual
`net10.0-windows` Host directory. Normal restore/build and the corrected executable
path passed. Neither failed invocation established a product regression.

Frozen tree `e011833e296ed850f7245c7bc276619f9cc7916b` contains 2063 files.
Source manifest (`artifacts/hmi-effects-01/source-manifest.json`) SHA-256:
`dd7fab8125b02c51b72adf362a5eccb0f3dc2cb7f5e7943cd38a00bfbb4b8e2c`; source ZIP SHA-256
`8ed8748b6075ed724a1da2ac2f4ecf9a47e6e2e15e290fe3377997e97ca0dfb6`.

The full aggregate (`artifacts/hmi-effects-01/source/artifacts/aggregate/result.json`)
passes 42 managed suites, 1,455 Python tests (one missing-native-evidence skip),
24 Explorer model/client checks and all MCP workflows. This includes 166 component
checks and 204 component MCP checks across two clean Host lifetimes. Contracts and
source bindings pass with 204 tools, 56 mutation kinds and 278 features. Run:
2026-10-17T18:43:06.1491854Z to 2026-10-17T19:01:28.3561207Z. Receipt SHA-256:
`a3f9fe779a3c1b2db5dc2afb563992a8a2be61326decc707b9261e90738581c0`.
Console (`artifacts/hmi-effects-01/aggregate-console.log`).

All 25 package stages (`artifacts/hmi-effects-01/source/artifacts/package-checks/result.json`)
pass, including original/relocated execution, ZIP/manifest checks and the v3 Host
workflow. Bundle `tia-mcp-offline_20261017_184753_1133e201` has 1455 manifest files.
Manifest SHA-256: `e8ae427b9a74ddacb3e5bbd4e63a55a93720af022956ed044cf8e588818cad2c`.
Package receipt SHA-256: `9ea976aaab51b6c0620077a8a9e052751ebe8d89b744a4566740f0bfece10751`.
Console (`artifacts/hmi-effects-01/package-console.log`).

The prior-build compatibility check (`artifacts/hmi-effects-01/legacy-compat/verification.json`)
compares the previous and new binaries against six actual retained v1/v2 records,
all existing sections and retained forged-record refusals. Responses match; original
evidence remains byte-identical, and v3 sections refuse on legacy records. Both Host
lifetimes exit cleanly. The current hashes match the full aggregate's Host/Core.

The final comparison (`artifacts/hmi-effects-01/final-source-comparison.json`)
permits only completion-document edits after the tested tree and preserves all 106
unavailable historical evidence binding rows. The integration receipt (`artifacts/hmi-effects-01/integration.json`)
records exact Git/CI/cleanup status. No browser visuals were repeated because this
increment changes no UI. No native SDK compilation or native execution occurred.


Remaining: independently obtained native outputs/complete peer inventory, genuine
library/template/instance identity, semantic and supported-format effect expectations,
generation/regeneration, compile, fresh reopen, archive recovery and native visual/runtime
acceptance. Supplied equal bytes cannot prove a native operation happened. Full SV-02,
C01/D15 and the complete 44-workstream release scope remain open.
