# Component-to-HMI supplied-evidence review — 17 October 2026

Base: `9280f0ddd43477fcf0707b1d32f38823badd1017`.

The new [workflow](../reference/hmi-component-review.md) implements the portable
C01 review increment: exact saved component/destination identities, explicit
multi-object interface comparisons, selected member/output mappings, independently
retained baseline/current/proposed files and conservative conflict/gap reporting.
The full C01 install/regeneration scope remains open.

Pre-freeze verification passes 96 managed checks and 81 public MCP checks across
two clean Host lifetimes. Tests cover install/update, changed interfaces beyond
bound objects, protected/ambiguous sources, output ancestry and collision handling,
manual ownership and independent byte drift, exact opaque companion files,
malformed/oversized input, paging, upload release/restart, storage quota including
incomplete files, tampering and rehashed false claims. Contract checks report
204 tools, 56 mutation kinds and 278 features. No Siemens binaries are required.

The first added quota test accidentally reused released upload references and
failed before reaching the capacity check (`hmi-component-managed-04`). Its fixture
now reuploads the original independent files before checking retention capacity;
`hmi-component-managed-05` and `hmi-component-mcp-03` pass. No production bypass was
introduced and the failed evidence directory remains retained.

Frozen tree: `91b4a5a5b36cc8ae4a21d96e8bd22d07744dceb8` (2058 files).
Source manifest (`artifacts/hmi-component-01/source-manifest.json`) SHA-256:
`be4b88e3bd8147dd1f29450b45218e518bef66cc8bb365990e0f64ac11a98314`.
Source ZIP SHA-256: `4ab2fce10f1eb1ecbfd0aba07c69b42573987e917f054288e4355db29be8ac36`.

The full offline aggregate (`artifacts/hmi-component-01/source/artifacts/aggregate/result.json`)
passes all 42 managed suites, 1,455 Python tests (one unavailable-native-evidence
skip), 24 Explorer model/client checks and all public MCP workflows. This includes
96 component/HMI checks, 81 component/HMI MCP checks across two clean Host lifetimes,
226 general checks and 35 diagnostic groups. Contracts/source bindings pass with
204 tools, 56 mutation kinds and 278 features. Run: 2026-10-17T17:41:18.4822159Z to
2026-10-17T17:55:01.6043367Z. Receipt SHA-256: `a30650fcd4a25401ecace49ab0bbe7ab4a06bc231b6e001a7b44a26892d55e9d`.
Aggregate console (`artifacts/hmi-component-01/aggregate-console.log`).

All 25 package stages (`artifacts/hmi-component-01/source/artifacts/package-checks/result.json`)
pass, including original/relocated MCP, exact ZIP/manifest verification and the new
component/HMI install/update workflow. Bundle `tia-mcp-offline_20261017_174141_b4e4f5a8` contains
1452 manifest files. Package receipt SHA-256:
`8e911caf6405993ca19669abb48cd1331e4d22e2048a7109c9c3360bb42719c4`.
Manifest SHA-256: `fa34289bda0b55c577dad9db7b7f6e41ab2d3707b4f627b7aab854717ec77cab`.
ZIP SHA-256: `467a3b79fb14c0d7eb5a5b0f914a2adfa13d62aa73fb1dcebcadcd165707350c`.
Package console (`artifacts/hmi-component-01/package-console.log`).

The final source comparison (`artifacts/hmi-component-01/final-source-comparison.json`)
allows only completion-document changes after the tested frozen tree and preserves
all 106 unavailable historical evidence binding rows. The
integration receipt (`artifacts/hmi-component-01/integration.json`) records exact
Git/CI and cleanup state. Browser visual tests were not repeated: no UI was changed.


No native SDK build, TIA/PLCSIM execution, Siemens export compatibility, library
installation, generation, compile/recovery, visual/runtime or full-project
preservation acceptance is claimed. All source authenticity/complete coverage/
native readiness flags remain false. Existing production native gates are unchanged.
