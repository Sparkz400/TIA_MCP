# Unified internal-tag creation repair — 23 October 2026

Review of the remaining HMI library-query contract work exposed a production
adapter mismatch. The retained native Unified foundation probe observed
`Connection: "<Interne Variable>"` for a new internal tag. The pure mapping rule
and foundation probe already preserve this localized default, but
`ApplyUnifiedHmi` still required an empty connection and therefore rejected it
after creating the tag. HMI query schema work remains open.

The production adapter now uses `HmiInternalTagCreation.Create`, a shared boundary
without optional HMI type references. It creates once, captures the connection
before setting the type, then captures name, type, connection, PLC tag and the
configured-connection lookup. Exact name/type and unchanged default are required;
a nonempty PLC tag or configured connection refuses the result. Native exceptions
propagate to the existing enclosing recovery journal; the helper never retries or
deletes the created tag after a failed readback.

The result retains `defaultConnection`, `plcTag` and
`nativeConnectionResolved: false`, together with the original result fields and
`runtimeVerified: false`. Offline operation validation accepts localized labels
only with all three observations and an exact matching default. Legacy results
without the added fields still require an empty/null connection.

## Verification

Evidence is under `artifacts/todo-unified-tag-20261023/`; `verification.json` pins
the changed inputs, worker binary, original native report and test outputs.

- `baseline.json` and `original-tag-creation.txt` retain the original source pin
  and branch. The label comes from the unchanged
  `artifacts/library-hmi-unified-20261008-e/qualification.json`. That foundation
  report remains failed at its later compilation gate; it proves only the
  captured internal-tag label here.
- `managed-tests.log`: all 95 checks pass: 32 mapping rules, 45 existing link
  cases and 18 new creation-boundary cases. These cover German/English/null/empty
  labels, changed labels, real connections, PLC bindings, changed names/types and
  exceptions at every create/read/write/lookup boundary without replay.
- `framework-tests.log`: the same 18 new cases pass on actual .NET Framework.
  A separately compiled copy of the original production branch rejects the
  native-observed label using synthetic providers, reproducing the old failure.
  This is execution of the original branch, not a reconstructed predicate.
- `framework-operation-validation.json`: actual Framework helper output validates
  within a synthetic plan/change envelope. This is derived offline evidence,
  not an original native change response.
- `library-tests.log` and `wiring-tests.log`: 87 library tests and five caller
  wiring tests pass. These include malformed/partial sentinel observations,
  conflicting original labels and legacy compatibility.
- `native-replay.json`: all 32 retained native change/preview pairs still validate;
  the source report bytes remain unchanged. This is compatibility evidence only.
- `worker-build.log`: the complete V21 worker builds with zero warnings/errors.
- `optional-module-isolation-final.log`: `bridge_info`, `library_capabilities`
  and `library_query` with `hmi_capabilities` pass against that worker, while every
  optional WinCC assembly resolution is refused. The probe checks the Portal
  field before/after each call and rejects any loaded optional HMI module.
  Original JSON metadata is retained under the fresh directory named in the log.
- `contracts.log` and `bindings-validation.log`: coverage and source bindings
  pass for 261 tools, 56 mutations and 335 feature entries. Only source inventory
  inputs change; no feature or qualification status is promoted.

The first optional-module attempt failed because the older reflection probe
looked for a one-argument constructor; the current session has a second optional
Boolean parameter. The fixture now selects that exact constructor with `false`
and diagnoses missing entry points explicitly. Its original failure log remains
retained. An initial legacy compiler invocation also failed; the successful
Framework probe uses the SDK compiler targeting `net48`, matching the worker.

No TIA project, native engineering action, PLCSIM operation or installed package
was changed. Native creation/compile/save/reopen, full HMI preservation and
runtime qualification remain open. Earlier aggregate/package evidence belongs
to its recorded frozen baseline and does not cover this increment.
