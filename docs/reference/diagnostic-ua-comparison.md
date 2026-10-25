# Before and after UA captures

`diagnostic_ua_compare` compares two exact retained polling or subscription
prefixes [bound to diagnostic intent](diagnostic-ua-binding.md) before acquisition.
It contacts no server, starts no worker and never replays acquisition. Identical
prefixes and two prefixes of the same capture are explicitly labelled; neither
counts as an independent repeat or proof of a fix.

```json
{
  "beforeSnapshotRef": "<exact before snapshotRef>",
  "afterSnapshotRef": "<exact after snapshotRef>",
  "request": {
    "reason": "Compare recorded enable observations around two selected events.",
    "beforeAnchorSeconds": 1,
    "afterAnchorSeconds": 2,
    "windowStartSeconds": -0.5,
    "windowEndSeconds": 0.5,
    "signalMap": [{"before": "enable", "after": "enable"}]
  }
}
```

Both window endpoints are inclusive. Selection uses each capture's original
**Host elapsed time**, relative to its explicit anchor. Request values are within
±1,000,000 seconds at millisecond resolution; original fractional milliseconds
remain exact in retained rows and boundary comparisons. Source, server, event and
Publish timestamps remain unchanged. There is no automatic event selection,
synchronized-clock claim or source-time join. Rows outside the window remain in
the report.

Select 1–64 one-to-one pairs of exact prebound signal IDs. Saved datatypes and
caller unit labels must match; mappings do not certify online addresses, UA type
equivalence or physical identity. Changed saved reviews additionally require
`allowChangedSource: true` and a reason. Each side retains its experiment and
mappings. This descriptive comparison does not pair hypotheses or replace
experiment/replan acceptance.

## Compared evidence

- Good scalar Boolean counts and numeric ranges grouped by original built-in
  type, declared data type/rank/dimensions and observed encoding. An `Any` rank
  does not turn an observed scalar into an array or an observed array into a scalar.
  Minima/maxima retain original decimal
  strings, including full Int64/UInt64 precision. Numbers outside exact decimal
  arithmetic remain in the evidence and increment `numericNotRepresentable`;
  they never round into a statistic.
- Uncertain/Bad values, nulls, unsupported values, metadata failures and Host read
  errors remain separate counts. Arrays/matrices remain exact original evidence
  and are counted separately from scalar statistics. Counts overlap; they are not
  percentages or exclusive categories.
- Original event type, source node, source name and severity field tuples with
  before/after counts and differences. Null/status fields remain distinct. Tuples
  are **not alarm identities**. Repeated EventIds on distinct messages count as
  recorded notifications; duplicate envelopes stay visible without double counts.
- Original event selections/filters, event overflow, duplicate and repeated-ID
  counts. No event selection means **not monitored**, not no alarms. Counts are
  not normalized for window length, filter differences or delivery coverage.
  Event IDs are not joined between captures.
- Both complete [observation reports](diagnostic-ua-observations.md), original
  rows, plan/segment bytes/hashes, settings and progress. Clipping cannot hide
  full-prefix gaps or failures. Recorded rows spanning both requested boundaries
  do not establish continuous coverage or complete delivery.

Hypotheses and the verdict remain `unresolved`. No rates, interpolation,
continuous-state reconstruction, simultaneous samples, causal claim, successful
fix, physical qualification or source authentication are established.

## Exports and Explorer

The receipt returns a content-addressed report ID and downloadable artifact.
`diagnostic_ua_report_export(reportId, format)` accepts `json`, `markdown` and
`html`. JSON preserves exact report bytes; readable exports include the complete
comparison and both embedded captures. Export reopens both original chains,
bindings and experiments and rederives the comparison. Missing originals or
rehashed alterations refuse; embedded copies cannot substitute on the Host.

In portable Explorer, **Open capture** accepts the exported comparison JSON.
The viewer checks both original chains and independently recomputes mappings,
windows, statistics and event counts before replacing the current view. Inspect
before and after original rows separately, including rows outside the window, and
download the exact supplied JSON. Local consistency checks do not authenticate
the source or independently establish the claimed evidence origin.

Each source retains the observation limits of 10,000 rows and 3 MiB of original
chain bytes. The **combined comparison/export is limited to 8 MiB**, within the
existing diagnostic store quota. Oversized comparisons refuse without truncation
or a completed partial report. Select explicit smaller historical prefixes when
appropriate and describe their scope accurately. Existing observation reports,
trace v1/v2 measurements and Web runtime comparisons keep their formats.

Contracts: [comparison](../../contracts/diagnostic-ua-comparison.v1.schema.json),
[export](../../contracts/diagnostic-ua-comparison-export.v1.schema.json).
