# Component recipe text boundaries — 22 October 2026

The pure PLC component planner and public recipe contract now require complete
restricted identifiers and dependency hashes. A trailing newline can no longer
pass a simple-name, native-network-ID or SHA-256 check. Validation does not trim
inputs. General tag paths and native symbolic names keep their existing behavior.

The worker adapter already invokes recipe preflight before scope resolution and
component instantiation planning. This repair tightens that existing entry point;
it adds no mutation route and does not enable combined component installation.

Evidence root: `artifacts/overnight-20261022/component-boundary/`.

- `original-regression.log`: five new regressions fail on the original planner
  (208 passing/5 failing groups). Four recipe-field preflights accept a trailing
  LF; an invalid retained dependency hash also reaches a successful plan.
- `schema-original.log`: ten trailing-LF subcases fail on the original schema.
- `fixed.log`: all 213 managed component groups pass, including existing pure
  LAD/FBD builders, dependency capture, staged coordination, preservation contracts
  and the production apply guard. Six new groups cover four recipe fields with
  valid/maximum/invalid inputs and no rewriting, exact dependency hashes, and
  preservation of general symbolic tag names.
- `python-components.log` and `python-library.log`: 35 and eight tests pass,
  respectively. Both original recipe examples validate; 70 malformed-identity
  subcases refuse, zero GUIDs remain invalid, and general paths remain allowed.
- `ecmascript.json`: all six changed public patterns compile and pass 50 checks
  with JavaScript regular expressions.
- `contracts.log`: 261 tools, 56 mutation kinds and 335 feature entries remain valid.
- The original parser and schema are retained as `original-planner.cs` and
  `original-schema.json`; source registry collection/validation are recorded in
  their separate logs. Validation passes with no unavailable evidence and release
  acceptance false.

Only three planner regexes and six public schema patterns changed. The address
prefix check deliberately remains a prefix check. The previously repaired typed
payload parser is also linked into these component tests, so the full component
suite verifies its callers against the stricter typed grammar.

No Siemens product or native worker was started. Native library identity/content,
installation, compiler, persistence, recovery, runtime and full-release acceptance
remain open. This subsequent repair has focused current-tree verification; earlier
observation package receipts apply to their own frozen source sets.
