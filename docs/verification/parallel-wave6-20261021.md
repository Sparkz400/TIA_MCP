# Bounded UA subscription integration — 21 October 2026

Five read-only acquisition tools now implement bounded CreateSubscription/CreateMonitoredItems/Publish/Republish/DeleteSubscriptions on an existing explicitly trusted encrypted owned session. Immutable journals retain requested/revised settings, partial item failures, exact decoded values/status/timestamps, sequence gaps, duplicates, overflow and retransmission. Stop, interrupted reopen and export preserve uncertainty without automatic reconnect, transfer or replay. [Workflow](../reference/opcua-runtime-subscriptions.md) and [focused qualification](opcua-subscriptions-20261021.md).

## Combined qualification

The frozen source is commit `4d13c87dc2a9c8532677f44f687b98c742ae6268`: 2,777 files, manifest SHA-256 `6478e8005f21d4619d74d8340a95d8ea52ffbec29fb8faddb37577a6d9e2113e`. The enclosing PowerShell runners were awaited without forced termination. The original optimization test's timeout handler ended its own timed-out subprocesses; their timeouts remain failed outcomes. Source hashes match before and after all four recorded stages. The later configured-fixture recovery helper/tests have separate 117-check focused qualification and do not change Host, production runtime, subscription sources or shipped package bytes.

| Gate | Exact outcome |
| --- | --- |
| Original aggregate | Failed after 1,624 Python tests with one existing skip. Its only two errors are the `-O` and `-OO` optimization subprocesses exceeding the inherited 180-second deadline while package validation was also running. No assertion failure; managed and JS phases had not begun. |
| Optimization completion | The exact unchanged test passes both modes in 318.579 seconds total. The original per-child 180-second deadline is unchanged. The original failed aggregate remains failed. |
| Managed continuation | All 45 managed suites and every aggregate Host workflow pass on the exact frozen source. |
| Explorer | All 25 checks in the frozen aggregate's model/client command pass, zero skips. This count is distinct from the earlier 31-check independent relationship suite. |
| Portable package | Original full runner passes all 52 stages, including exact ZIP/manifest verification, original and relocated smoke, prior consumers and the subscription workflow. |
| Final UA runtimes | Aggregate: 474 managed, 66 session, 161 polling and 325 subscription checks. Package: 475 managed, 66 session, 161 polling and 321 subscription checks. Counts include their retained independent oracles and request guards. |
| Independent relocated consumers | Fifteen original/honestly-rehashed cases through status, page and export: 45 checks, fifteen clean owned Host exits, unchanged original and package bytes. |

This is a qualified continuation of the failed original aggregate, not a claim that a fresh all-in-one aggregate passed. The console, original failure and unchanged completion receipts remain under `artifacts/pw21w6s1/artifacts/`. [Final verification](../../artifacts/goal-wave6-20261021/ua-final-verification.json) binds source, outcomes, runtime hashes, original prior evidence and exact package. ZIP SHA-256: `1c729dd9869e41fb2719b01e1dad232d9807c481aa6477d00b91eae783218f16`.

All six Explorer assets match the previously qualified viewer and both new package locations exactly. Its separately completed [80-import repeatability qualification](project-explorer-performance-20261021.md) and independent browser/relationship evidence remain applicable to those unchanged assets; no redundant browser/performance run is claimed here.

## Review and preservation

Independent review reproduced honestly rehashed contradictory terminal/sequence/scalar evidence, which now refuses through all retained consumers. Integration also reproduced unsupported request/node options being silently ignored; the scoped deserialization guard rejects them before a session RPC or retained intent, while shared polling/node behavior stays unchanged. Generated request descriptors preserve their node shape and defaults.

The original Host crash no-network race and audit-partition test failure remain in the focused record. The repaired fixture waits only for exact original accepted Publish handles to settle before measuring no replay. Earlier actual producers remain accepted, altered contradictions refuse and original bytes stay unchanged. Local journal durability does not claim a server durable-subscription feature or clean remote logout after a killed Host.

The configured native prerequisites/recovery and portable Explorer repeatability are separate milestones with their own evidence. Historical reporter cleanup remains unknown and default quarantine refuses; explicit isolated owned native work has its own admission, process and failure records. This subscription gate performs no native or physical operation.

## Remaining work

Prebound diagnostic observation/report integration proceeds separately. Physical CPU/server qualification, independent program/address/clock association, wider device profiles, actual disk-full, reconnect/transfer and separately admitted writes/control remain open. All-workstream release acceptance and hosted branch checks remain independent gates.
