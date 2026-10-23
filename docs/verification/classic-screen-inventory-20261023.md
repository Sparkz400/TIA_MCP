# Classic screen inventory — 23 October 2026

Ordinary Classic screen inspection now includes regular screens, templates,
popups, slide-ins, global elements and overview screens. The adapter uses the
installed public SDK properties and the same bounded traversal exercised by the
offline fixture. This closes the implementation portion of the corresponding
HMI TODO; native acceptance for each device/runtime profile remains open.

Rows retain `kind: hmiScreen` and add an optional `screenRole`. Canonical paths
separate each role and preserve escaped nested folder names. Repeated names in
different roles remain different objects. `coverage.screenScopes` reports each
visited property, role, path, observed count and complete/absent/unavailable/
truncated status. An absent singleton differs from an unreadable folder.
Failures preserve earlier rows and allow independent scopes to be inspected.
Exact screen lookups restrict traversal to their own role; an unavailable
unrelated template folder cannot invalidate an ordinary-screen lookup.

The traversal shares the existing visit and retained-byte bounds, charges roots
and folder entries, stops after retention refusal, disposes enumerators and
bounds folder depth. Missing names and duplicate paths cannot invent identities.
Explorer capture retains the role metadata and canonical ancestors. Detail
routing requests screen metadata for the added roles; their item parsing and
source-export routes are not qualified by this change. Existing regular-screen
item inspection remains separate. No HMI mutation or preservation gate is opened.

## Verification

Evidence is retained under
`artifacts/overnight-20261022/classic-screen-inventory/`:

- `inspection-03.log`: 99 inspection checks, including all six scopes, nested
  paths, selected-scope lookup, getter/enumerator failures, duplicate paths,
  depth and visit limits, and immediate retention refusal.
- `model.log`: 33 model checks, including regular/additional-role detail routing
  and compatibility with existing records without a role.
- `python-hmi.log`: 72 Python tests. Four new schema tests validate the actual
  helper-emitted synthetic fixture and reject invalid roles/status/counts.
- `hmi-contracts.log`: four existing HMI contract tests.
- `sdk-final.log` and `host-build.log`: complete SDK worker and Host builds,
  each with zero warnings/errors. The worker was compiled, never executed.
- `host-contracts.json`: actual Host initialize/list/catalog probe under
  `--no-native-workers`; 241 tools exposed in that Host mode, the updated HMI
  description, unchanged unverified native qualification, and clean Host exit.
- `contracts.log` and `bindings-validation.log`: repository coverage validates
  261 declared tools, 56 mutation kinds and 335 features, with no unavailable
  evidence references. `releaseAccepted` remains false.
- `verification.json`: exact source, fixture, log and built-worker hashes.

The first inspection run failed because a Newtonsoft nullable string is a
string-typed JValue containing null. The assertion now tests its nullable value;
`inspection-01.log` remains retained. The production behavior did not require a
change for that assertion. Subsequent focused runs pass.

The fixture is synthetic and supplies no native authority. No TIA/PLCSIM process,
native worker, native project read, screen import, runtime, or native visual
assessment was performed. Earlier aggregate/package results apply only to their
recorded source snapshots; this increment has the focused checks above.
