# Retained diagnostic experiments

`diagnostic_experiment_plan` composes an existing immutable diagnostic case, a
justified watch plan and a manual trace specification. `diagnostic_experiment_review`
then joins exact imported before/after measurements, sample prediction analyses
and an explicitly aligned comparison. Both tools are offline and require no
Siemens installation.

Start with `diagnostic_plan` using the exact saved review, FB, instance DB/member
path, hypotheses and justified signals. Optional `alarmContext`/`alarmBindings`
and `testSuiteFinding` keep their original supplied evidence and limitations.
The experiment retains the complete case, including its captured call chain,
signal declaration evidence, bug report, alarm associations and Test Suite
counters. It re-reads the original Test Suite receipt instead of trusting a
copied finding summary.

## Prepare the experiment

```json
{
  "planId": "<diagnostic_plan.id>",
  "request": {
    "reason": "Observe command loss beside the final output.",
    "watch": {
      "tableName": "Conveyor diagnosis",
      "tiaVersion": 21,
      "externalStorage": [
        {
          "objectPath": "/projects/Fixture/plc/CPU/blocks/DB_IO",
          "reason": "Compare final output with the selected instance command."
        }
      ]
    },
    "trace": {
      "backend": "manual",
      "family": "ordinaryPlc",
      "reviewId": "<exact saved review ID>",
      "settings": {
        "task": "Main (requires target verification)",
        "sampleEvery": 1,
        "preSamples": 100,
        "postSamples": 100,
        "rationale": "Observe the command loss with preceding context.",
        "trigger": {"signalId": "final", "kind": "fallingEdge"}
      }
    }
  }
}
```

The response contains `id`, a hashed downloadable `artifact` and `experiment`.
The experiment exposes `watchPlanId` and `tracePlanId`. The `tests` array links
each hypothesis to its declarative prediction and exact signal reasons;
`compositionGaps` explains missing selected alarm/Test Suite evidence or missing
predictions. Existing detailed source/coverage gaps stay in `diagnosticCase`.
Absent optional evidence does not become inferred evidence.

Watch and trace requests use their existing contracts. Unresolved explicit alarm
bindings, unavailable signals, missing calls and undeclared external storage
still refuse. Native configuration, signal observability, settings, firmware,
resource limits, transfer, arming and acquisition remain separate qualifications.
The tools do not execute Test Suite jobs, apply watch tables, configure traces,
write stimuli or operate a PLC.

## Import and compare

Import each original measurement through `trace_read` with the experiment's exact
`tracePlanId`. Committed upload input is retained independently, so releasing the
upload does not remove measurement provenance. JSON and explicit UTF-8 CSV
mapping retain their existing boundaries.

Call `diagnostic_experiment_review`:

```json
{
  "experimentId": "<before experiment ID>",
  "request": {
    "afterExperimentId": "<after experiment ID; may equal before>",
    "beforeMeasurementId": "<before trace_read.id>",
    "afterMeasurementId": "<after trace_read.id>",
    "comparison": {
      "reason": "Compare caller-selected command-loss event windows.",
      "beforeAnchorSeconds": 0.02,
      "afterAnchorSeconds": 0.02,
      "windowStartSeconds": -0.02,
      "windowEndSeconds": 0.03
    }
  }
}
```

An unchanged diagnostic case may use the same experiment or different requested
capture settings. Different cases require direct `diagnostic_replan` lineage
from the before case. The tool checks every replan mapping against both resolved
signal sets and preserves the hypotheses and role template. Explicitly select
alarm/Test Suite evidence anew for the new target; omitted evidence remains only
in the old case.

Different source reviews or declared program identities additionally require
`comparison.allowChangedSource: true`. Different storage or instances require
an explicit one-to-one `comparison.signalMap: [{"before":"id","after":"id"}]`.
Equal signal names never establish equipment identity. Multi-hop replan chains
are not implicitly traversed; compare adjacent explicit experiments.

Review re-opens and rederives the experiment, watch/trace documents, case source,
Test Suite evidence and measurement normalization from the original input bytes.
Missing originals, hash tampering and rehashed alterations of derived evidence
refuse. Historical imported quality assessments are recomputed without
overwriting their originals. Validation completes before publishing derived
reports. Capacity or I/O failures may leave reusable valid content-addressed
children; no partial experiment is returned.

The response contains the immutable `review` and its artifact. `beforeReportId`,
`afterReportId` and `comparisonId` identify reports supported by
`diagnostic_report_export` in JSON, Markdown and HTML. Download the complete
experiment/review using `artifact_read`; the composite itself is not a new
readable-export format.

The review distinguishes supported/contradicted sample predictions from
unresolved evidence and retains each acquisition context and quality assessment.
An absent fault remains unresolved. `bugFixProven`, `causationProven`,
`sourceAuthenticityVerified`, `clockSynchronizationVerified` and
`nativeQualification` remain false. Alignment does not synchronize clocks;
caller-attested quality does not authenticate a program or establish causality.

Contracts: [experiment](../../contracts/diagnostic-experiment.v1.schema.json),
[review](../../contracts/diagnostic-experiment-review.v1.schema.json).
See the [existing diagnostic workflow](../guides/trace-diagnostics.md) and
[verification](../verification/diagnostic-experiments-20261020.md).
