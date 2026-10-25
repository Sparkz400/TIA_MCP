# Retained UA before/after comparison — 25 October 2026

The [comparison workflow](../reference/diagnostic-ua-comparison.md) adds one
offline tool and extends the existing UA export/Explorer routes. It reopens both
original capture chains and diagnostic bindings, compares explicit Host-clock
windows and signal mappings, and retains exact original evidence beside Good
scalar statistics and recorded event-field counts. Existing observation and trace
formats are unchanged. No dependency, worker or native acquisition route is added.

Implementation is separate from Claude's component and HMI work. Main through
`b965eff` is incorporated, including catalog integration #64, component PR #62 and
journal-date fix #67. Generated source bindings are recomputed from the combined
source instead of replacing either implementation.

## Evidence

Evidence is preserved outside the task checkout under
`artifacts/ua-comparison-20261025` in the primary checkout.

- `managed-frozen.log`: diagnostics pass all 47 groups, including 48 focused
  comparison checks covering exact
  UInt64 ranges, Good/Uncertain/null/error separation, selected and excluded rows,
  empty/running prefixes, self-comparison labels, explicit mapping refusal,
  changed-review admission, missing originals, honestly rehashed alterations,
  numeric range/precision exclusions and the combined 8 MiB quota. An individually
  valid large observation is refused as an oversized comparison without publishing
  a partial diagnostic artifact. Vectors also cover distinct anchors, inclusive
  boundaries, fractional milliseconds immediately outside a window and scalar /
  array values under `Any` rank, with original rank/dimensions retained.
- `host-frozen/result.json`: 2,098 checks passed through actual Host tools and an
  independent encrypted loopback server. Comparisons cover polling/subscription,
  different event severities/counts, repeats, duplicate envelopes, gaps, overflow,
  unavailable Republish and denied monitoring. Original bytes are independently
  downloaded and checked. Exports/missing-source refusal and a second Host produce
  the same immutable comparisons; server audits show no comparison/export calls.
  Poll-loop check counts can vary with scheduler timing.
- `browser-security/result.json`: 337 checks passed on generated portable assets,
  desktop/mobile layouts, before/after original rows, exact JSON download, inert
  HTML/Markdown and independent refusal of altered windows, counts, ranges,
  mappings, event groups, source bytes and authority claims. The actual comparison
  screenshot was visually inspected. Numeric exclusion, empty-prefix, alignment,
  fractional-boundary and `Any` rank vectors were checked in the browser model.
- Explorer tests pass 30 checks; two existing optional fixture checks are skipped
  when their external managed-export fixture paths are not supplied. Both new
  comparison contract tests, all three observation contract tests and portable
  asset packaging checks pass.

The full package and current-head hosted CI receipts are separate gates, recorded
alongside these focused checks when complete. Passing them does not close native
Siemens, physical-server, clock synchronization, complete event delivery, source
authentication or causal/fixed-fault acceptance.

## Preserved investigations

`host-1` exposed a schema mistake: subscription Host times include fractional
milliseconds. The schema now retains numbers and Explorer compares them using
exact decimal arithmetic. `host-2` exposed a checker assumption that notifications
on lifecycle rows were a list rather than null; the independent checker now treats
those rows as having no value notifications. `host-3` passed before the final
readable-export formatting adjustment; `host-final` verifies the adjusted output.

The first Explorer test ran before the portable bundle was regenerated because a
pack invocation lacked its required input argument. Rebuilding with `--empty`
restored the generated-asset equality check. `browser-1` used named rather than the
script's positional arguments and stopped before execution; later browser runs
passed. `managed-final` used a non-hex fixture capture ID and was correctly refused;
the fixture was corrected and `managed-final2` passed. These earlier failures are
not presented as passing evidence.

The first full package run was intentionally interrupted during source review:
statistics must use observed encoding, not assume declared rank -1 is the only
scalar case. Regression vectors now retain scalar and array values under `Any`
rank separately, and numeric groups retain original rank/dimensions. The owned
process identities and confirmed exits are recorded in
`interrupted-package-processes.json` and `interrupted-package-exit.json`.
That interrupted package is not passing evidence. The portable reader also no
longer mistakes an Int64/UInt64 array envelope for a scalar decimal string.

CodeQL alert #8 identified unescaped event-count cells in the comparison renderer.
All three cells now use the existing HTML escaping helper. The browser regression
mutates those three values after import to check the renderer independently:
`browser-security-before` fails because elements were created; `browser-security`
requires literal text and no injected elements. Numeric import validation remains
in place. The second full package run was stopped before this fix; owned process
identities and confirmed exits are in `codeql-package-interruption-processes.json`
and `codeql-package-interruption-exit.json`. That interrupted run is not passing
evidence. Fresh CodeQL analysis must confirm the fix; the alert is not dismissed.
