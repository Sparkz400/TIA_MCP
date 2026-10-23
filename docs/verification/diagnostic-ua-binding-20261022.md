# UA diagnostic binding prerequisite — 22 October 2026

The local working tree adds `diagnostic_ua_binding_plan` and optional
`diagnosticBindingId` on polling and subscription capture requests. Explicit
experiment, session-attempt and ordered-node mappings are revalidated before
leasing a session or retaining a new capture intent. Original unbound request
serialization is unchanged; adding/removing a binding on an existing capture
refuses. See the [workflow](../reference/diagnostic-ua-binding.md).

## Verification

The focused run is retained under `artifacts/overnight-20261022/`. The final
rebuilt Host has zero build warnings/errors. Final validation passes 54 new
managed checks, all 45 diagnostic groups, 475 UA runtime checks and 93 actual
Host/loopback checks, including the rebuilt embedded feature catalog. Logs:
`final-host-build.log`, `final-diagnostics.log`, `final-ua-runtime.log`;
machine-readable receipts: `ua-runtime-final/result.json` and
`binding-mcp-final/result.json`. The Host receipt retains exact loaded-directory
binary hashes and both original Host clean-exit receipts; the original server
also exits cleanly. Earlier 65- and 91-check qualification receipts are retained.
These are local focused checks, not a claim of a fresh full aggregate or
relocated-package pass.

The wire-contract tests pass 3 new cases and all 10 existing subscription cases;
all 30 API binding tests pass. Contract coverage includes 259 MCP tools,
56 mutation kinds and 333 features. Source binding regeneration initially refused
four edited JSON files with CRLF; normalizing those files to repository LF and
rerunning succeeded. No native evidence was promoted by registry regeneration.

The checks independently exercise selected source storage/member identity,
duplicate/missing/reordered mappings, changed session attempts, unknown fields,
malformed node identities, honestly rehashed derived-document changes, source
tamper, exact scalar preservation, no session/capture intent on rejection, actual
subscription cleanup, restart readback and no network replay. A saved PLC UInt
mapped to a fixture UInt64 remains explicitly unverified while the original
`18446744073709551615` value is retained exactly. Missing diagnostic source after
capture does not prevent raw evidence inspection or cause acquisition replay.

Both aggregate and package runners now include this actual Host workflow. Their
complete new runs have not yet been performed for this increment.

## Scope still open

This completes the pre-acquisition binding prerequisite, not all of D16/D17/C04.
The next increment must revalidate these bindings while composing complete
original polling/subscription prefixes into readable and Explorer reports.
Independent online program/address association, physical device profiles,
clock/scan guarantees, native access and behavioral assertions remain unqualified.
