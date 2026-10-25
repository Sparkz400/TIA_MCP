# Retained UA before/after comparison — 25 October 2026

The [comparison workflow](../reference/diagnostic-ua-comparison.md) adds one
offline tool and extends the existing UA export/Explorer routes. It reopens both
original capture chains and diagnostic bindings, compares explicit Host-clock
windows and signal mappings, and retains exact original evidence beside Good
scalar statistics and recorded event-field counts. Existing observation and trace
formats are unchanged. No dependency, worker or native acquisition route is added.

Implementation is separate from Claude's existing-caller component PR #62. Main's
catalog integration #64 was incorporated; the generated source bindings were
recomputed from the combined source instead of replacing either implementation.

## Evidence

Evidence is preserved outside the task checkout under
`artifacts/ua-comparison-20261025` in the primary checkout.

- Managed diagnostics pass all 47 groups. Focused comparison checks cover exact
  UInt64 ranges, Good/Uncertain/null/error separation, selected and excluded rows,
  empty/running prefixes, self-comparison labels, explicit mapping refusal,
  changed-review admission, missing originals, honestly rehashed alterations,
  numeric range/precision exclusions and the combined 8 MiB quota. An individually
  valid large observation is refused as an oversized comparison without publishing
  a partial diagnostic artifact. Later vectors add distinct anchors, inclusive
  boundaries and fractional Host milliseconds immediately outside a window.
- `host-final/result.json`: 2,104 checks passed through actual Host tools and an
  independent encrypted loopback server. Comparisons cover polling/subscription,
  different event severities/counts, repeats, duplicate envelopes, gaps, overflow,
  unavailable Republish and denied monitoring. Original bytes are independently
  downloaded and checked. Exports/missing-source refusal and a second Host produce
  the same immutable comparisons; server audits show no comparison/export calls.
  Poll-loop check counts can vary with scheduler timing.
- `browser-final/result.json`: 335 checks passed on generated portable assets,
  desktop/mobile layouts, before/after original rows, exact JSON download, inert
  HTML/Markdown and independent refusal of altered windows, counts, ranges,
  mappings, event groups, source bytes and authority claims. The actual comparison
  screenshot was visually inspected. Numeric exclusion and empty-prefix vectors
  were also checked in the browser model.
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
