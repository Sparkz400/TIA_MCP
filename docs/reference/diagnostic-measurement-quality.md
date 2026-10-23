# Versioned measurement quality and timing

`trace_read` accepts `trace-measurement.v2` JSON for individually timed supplied observations. The existing tool names also analyze, compare, compose single and repeated experiments, and export these records. The v1 JSON/CSV routes, their retained records and existing exports keep their original behavior. Existing records are never rewritten or automatically upgraded.

This route is separate from [retained runtime acquisition](diagnostic-runtime-acquisition.md). It does not acquire data, attach to a device, infer online addresses, convert a polling cycle to a simultaneous scan, or depend on the OPC UA client. Its sequence groups, clock labels, epochs, program identity and quality fields are supplied evidence. Every v2 behavioral verdict remains `unresolved`, including when the input claims complete, atomic or program-matched acquisition.

## Input contract

Use [trace-measurement.v2](../../contracts/trace-measurement.v2.schema.json). The shared output definitions are in [trace-quality-types.v2](../../contracts/trace-quality-types.v2.schema.json).

| Field | Meaning |
| --- | --- |
| `tracePlanId`, `reviewId` | Exact saved trace specification and source review. Saved declarations, case and trace specification are rederived on import and reopen. |
| `mode` | `synthetic`, `polling` or `manualNativeExport`. Polling must explicitly set atomic and scan-synchronized claims to false. |
| `clocks` | 1–8 named clock domains with supplied label, `source`/`host`/`supplied` kind and `s`/`ms`/`us`/`ns` unit. Equal names do not establish synchronization. |
| `capture` | Explicit `complete`, `incomplete`, `truncated` or `unknown` state, reason and nonnegative dropped count or explicit null. |
| `expectedIntervals` | Up to 64 explicit clock/epoch intervals, in that clock's original unit. An omitted interval makes adjacent change inference unavailable. |
| `units` | Exact saved signal IDs and units; no implicit conversion. |
| `samples` | 0–10,000 sequence groups in original order. Sequence numbers strictly increase within 0–10¹². An empty capture or empty reading group is representable. |
| `readings` | A subset of exact saved signal IDs. Missing keys remain absent; no null or zero is inserted. |
| Each reading | Scalar/null `value`, `good`/`bad`/`uncertain`/`missing` status, explicit string/null `statusCode` and `error`, and 0–8 individual timestamps. |
| Each timestamp | Declared `clockId`, explicit `epoch`, and exact numeric time or null. Each reading has at most one timestamp per clock. |

Good nonnull values must match the saved Boolean/numeric declaration. A bad or uncertain reading may preserve an undecodable scalar string. A usable value has status `good`, a nonnull value and no error. Other values never enter descriptive ranges, Boolean counts or matched/contradicted counts. A good untimed value can contribute an individual value statistic but cannot establish a timed change or membership in a requested window.

Irregular, repeated and backward timestamps are representable. Unknown timestamps, missing timestamps, absent readings, bad statuses, error text, truncation and unknown drops survive normalization and reports. Unknown fields, duplicate JSON property names, duplicate clock IDs, duplicate timestamps for one clock, unknown signal IDs, invalid types, unsorted/duplicate sequence numbers and unsupported precision are malformed and refused.

The JSON source is limited to 8 MiB. Timestamp values are bounded by ±10¹⁸ in the original unit and must be exactly representable by the supported decimal arithmetic. Conversion to seconds and anchor subtraction must remain exact; a comparison refuses a lossy conversion instead of rounding it. `manualNativeExport` additionally requires the original hashed artifact (up to 32 MiB), conversion explanation and readback evidence. That original artifact is revalidated; the combined v2 export includes the byte-exact supplied measurement JSON and its native-original reference, not an implicit native decoder or a bundled copy of every native source artifact.

## Import, analysis and windows

1. Prepare an exact trace specification with `trace_prepare`, or use the trace ID retained by `diagnostic_experiment_plan`.
2. Upload the v2 JSON and call `trace_read(tracePlanId, uploadRef)`, or pass an existing host-owned hashed `artifact`. After full input validation, v2 retains an independent byte-exact `.input` copy, including whitespace. The upload may then be released.
3. Call `trace_analyze(measurementId)`. The retained [trace-import.v2](../../contracts/trace-import.v2.schema.json) and [diagnostic-report.v2](../../contracts/diagnostic-report.v2.schema.json) include original reading content and structural quality assessments.
4. Call `trace_compare` with both v2 measurement IDs and explicit clock/epoch/window options:

```json
{
  "reason": "Compare recorded observations in explicitly selected epochs.",
  "beforeClockId": "source",
  "beforeEpoch": "boot-0",
  "afterClockId": "source",
  "afterEpoch": "boot-1",
  "beforeAnchorSeconds": 0,
  "afterAnchorSeconds": 0,
  "windowStartSeconds": -0.01,
  "windowEndSeconds": 0.05
}
```

Each selected clock and epoch must actually occur in its input. Anchors and requested window boundaries are in seconds. Selection uses each signal's individual original timestamp; a sequence group is not an atomic state. Absent readings, missing clock readings, unknown times, other epochs and observations outside the requested window have separate `selectionExclusions` counts. The complete selected epoch assessment remains alongside the clipped window, so choosing a smaller window cannot hide that epoch's reset/gap evidence. Untimed selected-epoch observations remain explicit in `untimedSelectedReadings`.

[trace-comparison.v2](../../contracts/trace-comparison.v2.schema.json) retains both complete acquisition contexts and quality assessments. Changed program labels or source reviews require `allowChangedSource:true` with a reason. Changed storage or instances require an explicit one-to-one `signalMap`; types and units must match. Missing mappings refuse. Mixed v1/v2 measurement pairs or repeated batches refuse with `MeasurementVersionMismatch`; no guessed conversion is performed.

Analysis preserves useful Boolean counts, numeric minima/maxima and declarative condition counts. Condition counts describe supplied sequence groups, not verified simultaneous states. Recorded value-change endpoints require two good, timed, consecutive sequence observations in the same epoch, strictly increasing times and the exact supplied interval. A bad/missing/uncertain/error reading, missing time, sequence gap, reset, duplicate time, epoch change, irregular interval or missing expected interval breaks continuity. At most 200 endpoint changes are listed per signal/clock series; the total and `changesTruncated` remain explicit. No rate, interpolated crossing time, correlation, causal effect or successful fix is inferred.

## Single and repeated experiments

Pass the same comparison options to `diagnostic_experiment_review`. It produces [diagnostic-experiment-review.v2](../../contracts/diagnostic-experiment-review.v2.schema.json) when both measurements are v2. Exact experiment-to-trace membership, saved FB/call/instance identity and explicit direct-replan lineage remain mandatory.

`diagnostic_experiment_compare_runs` accepts 2–8 v2 pairs and produces [diagnostic-experiment-repeat.v2](../../contracts/diagnostic-experiment-repeat.v2.schema.json). Each pair retains its explicit clock/epoch mapping. Requested relative windows and signal mappings must agree across pairs. Reusing the same original input bytes cannot count as another run; native-export duplicates are checked against the original native artifact hash. Distinct hashes still do not verify experimental independence. Repeated counts preserve condition-observed runs, but all runs remain unresolved and supported/contradicted behavioral run counts stay zero.

## Reports and retention

`diagnostic_report_export` handles v2 analysis/comparison IDs. `diagnostic_experiment_report_export` handles v2 single/repeated IDs. Both accept `json`, `markdown` and `html`, producing a [diagnostic-quality-report-export.v2](../../contracts/diagnostic-quality-report-export.v2.schema.json) receipt. The JSON document is [diagnostic-quality-report.v2](../../contracts/diagnostic-quality-report.v2.schema.json), containing:

- Exact source report bytes in base64 beside its parsed content and hash.
- Every selected normalized measurement, byte-exact original input JSON, original hash and byte count.
- Saved experiment identities, every run's analysis, explicit mapping, quality assessment, window and limits.
- Explicit false source/clock/atomicity/behavioral/causal/fix/native authority flags.

Exports rederive the entire applicable original chain. Changed or missing retained JSON, source review, trace/case, child analysis or rehashed derived records refuse. The initially supplied external file can move after v2 has independently retained its original bytes; modifying the owned retained original cannot pass.

HTML escapes untrusted labels/status/error text, contains no script or remote resources, wraps long identifiers and provides keyboard-accessible horizontal table scrolling. Markdown escapes untrusted text and encloses the complete JSON in a dynamically sized code fence. Both show original per-reading timestamps/status/errors and completeness limits. Neither resolves URLs contained in evidence.

Each document or rendered export is bounded to 8 MiB and existing diagnostic retention remains 256 MiB. An over-limit export refuses without silently dropping observations. Invalid request validation precedes child publication. A later storage quota/I/O refusal can leave a valid immutable input or analysis child, but never returns a completed import/review/export. There is no automatic purge.

See [focused verification](../verification/diagnostic-measurement-quality-20261020.md) for independently authored vectors, actual MCP, browser and actual prior-producer checks.
