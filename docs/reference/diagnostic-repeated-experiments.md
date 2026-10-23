# Repeated diagnostic experiments and combined reports

`diagnostic_experiment_compare_runs` compares 2–8 explicitly paired retained experiments. It counts requested-window sample prediction verdicts per run. It does not establish statistical significance, independent trials, causation, or a successful fix.

Each pair names the exact before and after experiment and measurement IDs. Those experiments retain the original bug report, saved FB/call/instance, selected alarm and Test Suite evidence, watch plan, trace plan, and explanatory signal roles. Both phases must use the same case or a direct explicit replan. A second instance needs the retained replan mappings and an explicit comparison `signalMap`; matching signal names do not establish instance identity. Changed saved source or declared program identity also requires `allowChangedSource: true`.

```json
{
  "request": {
    "reason": "Compare two separately collected observations of the output-writer prediction.",
    "pairs": [
      {
        "id": "run-1",
        "beforeExperimentId": "<64-character experiment ID>",
        "afterExperimentId": "<64-character experiment ID>",
        "beforeMeasurementId": "<64-character imported measurement ID>",
        "afterMeasurementId": "<different imported measurement ID>",
        "comparison": {
          "reason": "Compare the explicitly selected event windows.",
          "beforeAnchorSeconds": 0.02,
          "afterAnchorSeconds": 0.02,
          "windowStartSeconds": -0.02,
          "windowEndSeconds": 0.03
        }
      },
      {
        "id": "run-2",
        "beforeExperimentId": "<same before case's experiment ID>",
        "afterExperimentId": "<same after case's experiment ID>",
        "beforeMeasurementId": "<another original imported measurement>",
        "afterMeasurementId": "<another original imported measurement>",
        "comparison": {
          "reason": "Use the same relative window with this run's explicit anchors.",
          "beforeAnchorSeconds": 1.02,
          "afterAnchorSeconds": 2.02,
          "windowStartSeconds": -0.02,
          "windowEndSeconds": 0.03
        }
      }
    ]
  }
}
```

Across the cohort, the exact before case and exact after case must remain the same. Relative window bounds and compared signal mappings/units must match. Individual anchors may differ and do not imply synchronized clocks. Acquisition settings may differ; `settingsEqualAcrossRuns` discloses that difference, and every phase retains its requested/observed acquisition context.

An original measurement cannot count twice, including across before/after phases. Duplicate retained import wrappers do not create further runs. For manual native exports the underlying original artifact hash is used; otherwise the input artifact hash is used. Distinct bytes still do not establish independent trials: `independentRunsVerified` is always false.

## Requested-window quality

Predicate counts are recomputed from original normalized samples inside the inclusive relative window using exact decimal time conversion. A recorded event elsewhere in the capture does not count. Each window retains matched, contradicted, and unknown counts, even when it cannot support a verdict.

A verdict remains `unresolved` when the capture is non-atomic, dropouts are unknown/reported, the caller has not attested a program match, any original sample value is missing, a sampling gap is detected, the expected interval is absent, the recorded span misses a requested boundary, or fewer than two samples fall inside the window. This deliberately conservative quality gate examines the whole capture, including defects outside the requested window. It does not require or claim scan synchronization.

`completeRecordedSpan` means the original first and last samples span the requested bounds. It does not prove continuous observation or that no event occurred between samples. Partial and empty windows are named explicitly. Quality fields are caller attestations with structural checks; source authenticity is not verified.

With sufficient quality, a prediction with at least one observed condition and no unknown predicate values can be `supportedPrediction` or `contradictedPrediction` for those samples. No observed condition, absent declarative prediction, or unknown values remains unresolved. The report sums run verdicts separately for each phase, plus condition-observed runs. It does not pool samples, use a majority vote, compute a p-value, or turn improved counts into a fix claim.

## Combined export

Call `diagnostic_experiment_report_export` with a retained `diagnostic-experiment-review.v1` or `diagnostic-experiment-repeat.v1` ID:

```json
{"reportId": "<64-character report ID>", "format": "html"}
```

Supported formats are `json`, `markdown`, and `html`. The JSON is a combined `diagnostic-experiment-report.v1` envelope containing the unchanged original report document, each exact experiment/case, full before/after analysis, comparison, original input references, and freshly recomputed requested-window assessments. The receipt's `sourceArtifact` references the exact original report bytes. Old single-pair v1 report IDs and bytes remain unchanged.

Readable exports show descriptive counts, exact saved evidence, explicit lineage, original acquisition quality, window verdicts, and raw signal observations. They also include the complete combined JSON. HTML is standalone, with no scripts or external resources; saved text is escaped. Markdown escapes report content and uses a sufficiently long code fence around the complete JSON.

Before any format is published, the implementation reopens the original saved review, selected Test Suite receipt, watch/trace plans, measurement input/native original, normalized imports, and saved derived analyses/comparison. It rederives those records and refuses altered or missing evidence. An honestly rehashed modified aggregate or derived record does not bypass that check. `sourceEvidenceRevalidated` reports this consistency check, not authenticity.

Requests are limited to 64 KiB; each report/export is limited to 8 MiB and uses the existing 256 MiB diagnostic retention bound. All pairs and output size limits are validated before child publication. A storage capacity or I/O failure can still leave valid content-addressed children without returning a completed report. Exports accept a retained ID, not a caller-selected output path.

Contracts:

- [`diagnostic-experiment-repeat.v1`](../../contracts/diagnostic-experiment-repeat.v1.schema.json)
- [`diagnostic-experiment-report.v1`](../../contracts/diagnostic-experiment-report.v1.schema.json)
- [`diagnostic-experiment-report-export.v1`](../../contracts/diagnostic-experiment-report-export.v1.schema.json)

Native acquisition, stimulation, independent-run verification, source authentication, clock synchronization, causal diagnosis, and physical/PLCSIM fix qualification remain outside this offline workflow.
