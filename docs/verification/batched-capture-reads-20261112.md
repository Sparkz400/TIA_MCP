# Batched block and tag reads in capture (12 November 2026)

`project_model_capture` and `project_index` now read each PLC block's and tag's attributes with one
`IEngineeringObject.GetAttributes` call instead of one native call per property
([`BatchedAttributeReads.cs`](../../src/TiaMcp.Openness.V21/BatchedAttributeReads.cs)). Results are
unchanged; the warm capture of a 2,582-node project is 2.8× faster.

Workstation LT-PRG2024-03, TIA Portal V21, CPU 1511-1 PN (`6ES7 511-1AL03-0AB0/V4.0`).
All projects are owned and were opened read-only.

## Where the time went

`scripts/native/ReadCostProbe` starts its own Portal without UI, seeds a project
(blocks as SCL sources, tags in tables) and times reads on it.

On 500 blocks and 2,000 tags, warm, as the median of 5 rounds:

| Read | Block | Tag |
|---|---|---|
| Walk the composition only | 0.08–0.10 ms | 0.10–0.13 ms |
| One property | 0.32 ms | 0.34 ms |
| 7 (block) / 6 (tag) properties, one call each | 1.76–1.87 ms | 1.45–1.50 ms |
| The same properties in one `GetAttributes` | 0.71–0.81 ms | 0.53–0.57 ms |

- One native property call costs about 0.23–0.25 ms. Both forms returned equal values for every object.
- The capture's own call sequence repeats reads: `Name` and `Namespace` three times per block, `Name` twice per tag. Replayed exactly, that cost 2.81 ms per block and 1.84 ms per tag.
- Replaying it batched cost 0.97 ms per block and 0.64 ms per tag. Worker-local work (node id hash, JSON) is 0.026 ms per node.
- The replay (5.1 s) matched the real warm `project_model_capture` (5.0–5.4 s) on the same project, so per-property calls were nearly all of the warm capture.

The first capture after opening a project is also slow for another reason: TIA loads objects on
first access. The project opened in 7.8 s, and finding the PLC took 2.4 s on a cold Portal.

## Change

- `BatchedAttributeReads.Block` reads `Name`, `Namespace`, `Number`, `ProgrammingLanguage`, `IsConsistent`, `IsKnowHowProtected` and `ModifiedDate` in one call.
- `BatchedAttributeReads.Tag` reads `Name`, `DataTypeName`, `LogicalAddress`, `IsSafety` and the three `External*` flags in one call.
- Both are used by the project-model capture (`CaptureModelBlock`, `CaptureModelTags`) and the metadata index walk.
- If a native type's batch throws, returns the wrong number of values, or returns values of the wrong type, that type falls back to the typed properties for the rest of the worker's lifetime. A result never depends on the batch succeeding.
- Managed tests cover the fallback with synthetic shapes ([tests](../../tests/TiaMcp.ProjectModel.Tests/BatchedAttributeReadsTests.cs)). Other tools that read single blocks or tags are unchanged.

## Native qualification

`local.project-model.batched-reads.20261112`:

- `scripts/qualify_batched_capture.py` ran a baseline worker built from `origin/main` (`4607a5a0`) and the changed worker behind one source-built Host.
- Each worker had its own new Portal without UI and captured each project four times, then took one `project_index` capture.
- Passed: every `metadataRevision` of both tools is identical between the two workers. Both Hosts and workers exited 0.

| Project (owned) | Nodes | Baseline cold / warm | Batched cold / warm | Warm speed-up |
|---|---|---|---|---|
| Probe, 500 FC/DB, 2,000 tags | 2,582 | 14.1 s / 5.9 s | 8.0 s / 2.1 s | 2.8× |
| Probe, 200 FC/DB, FB with instance DB, software unit with namespaced blocks, 1,000 tags | 1,290 | 8.7 s / 3.6 s | 6.6 s / 1.9 s | 1.9× |
| Safety scratch (F-CPU, safety program, know-how protected F-blocks, PROFIsafe drive) | 212 | 12.0 s / 0.75 s | 11.7 s / 0.71 s | 1.05× |

- Warm is the median of rounds 2–4, and single rounds vary by up to about ±25%.
- The safety scratch has few blocks and tags, so its time is dominated by hardware and unit nodes, which still read per property.
- The run does not show which native types fell back, if any. Revision equality shows only that the results are the same.

## Not covered

- Hardware items, data types, technology objects, HMI and library nodes still read per property.
- Cold capture is dominated by TIA loading objects.
- Single runs on one workstation; no larger or customer-shaped project was timed.

Retained artifacts:

- `artifacts/read-cost-probe/20261112T072505Z/` (probe reports)
- `artifacts/read-cost-probe/20261112T074327Z/` (second fixture)
- `artifacts/batched-capture/20261112T081332Z/report.json`
