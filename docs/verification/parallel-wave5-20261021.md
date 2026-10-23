# UA polling capture, HMI integer conversion and Explorer scope — 21 October 2026

The fifth parallel increment delivers the three workflows in the [authorized plan](../plans/parallel-wave5-20261021.md). All implementation and final package checks below pass. Consistency of Explorer first-viewport performance across runs, native/physical qualification and full 44-workstream release remain open.

## Delivered workflows

| Lane | Delivered behavior | Focused evidence |
| --- | --- | --- |
| D16 / C03 / C04 | Five bounded read-only UA polling-capture tools reuse the durable capture store. Original typed values, quality, timestamps and gaps survive stop, interruption, restart, page and JSON/CSV export without reconnect or acquisition replay. | [Workflow](../reference/opcua-runtime-capture.md), [qualification](opcua-polling-capture-20261021.md): 454 managed checks, 160 actual encrypted Host/DPAPI capture checks, 65 existing session checks, 62 actual prior Web-record compatibility checks. |
| C01 / D15 / X08 | Explicit v3 integer-value-preserving conversion across SInt/USInt/Int/UInt/DInt/UDInt with fixed identities and exact prior intervals/translations. Overflow, altered files and unrelated conflicts refuse. No PLC value is converted. | [Workflow](../reference/unified-text-list-integer-conversion.md), [qualification](hmi-integer-conversion-20261021.md): 461 managed checks, 83 retained cases and 23 explicit refusals, 446 new Host checks and original v1/v2 replay. |
| Explorer | Declared relationship providers, retained scope, bounded traversal and visible truncation; exact endpoints, cycles, unresolved evidence, duplicate refusal and absent/null virtual-path compatibility. Saved input accepts up to 50,000 edges while graph responses remain bounded. | [Workflow](../reference/project-explorer-relationship-scope.md), [qualification](project-explorer-relationship-scope-20261021.md): 13 C# groups, 31 JS checks, 9 schema checks and independent 1k/10k/50k-node-and-edge oracles. |
| Integration | 253 declared tool bindings, 56 mutation kinds and 327 feature entries; shared runner wiring and source-bound portable package. | Final verification (`artifacts/parallel-wave5-20261021/final-verification.json`). |

## Combined validation

The frozen checkpoint contains **2,749 files**. Manifest SHA-256: `8040e47095e5dc08b5feb92948f205c955dc2c79030e568ca99ee09e1a373ccd`. The aggregate runner exited 0; the initial package runner exited 1 after 49 passing stages. Both original processes were awaited without forced termination, and every frozen source hash matched before and after. Production source and shipped binaries remain unchanged. A separately retained test-only UA synchronization repair and these reporting documents were updated afterward; the repaired harness is qualified against the exact packaged runtime modules.

| Validation | Result |
| --- | --- |
| Full offline aggregate | 45 managed suites, 1,591 Python tests (1 existing skip), 25 original Explorer model/client tests plus 6 scope/parity checks; every Host workflow passed. |
| Package / Unicode relocation completion | 51 stages completed: 49 passed in the original attempt, then the repaired managed UA check and the remaining two actual-Host stages passed on the unchanged package. Original failure remains retained. Exact ZIP verification, original/relocated MCP smoke and all existing workflows passed. |
| New UA capture workflow | 160 independent Host checks on each final runtime; intentional Host crash/reopen retains the committed prefix. The enclosing runner exits normally; crash is not clean logout. |
| New HMI conversion workflow | 446 checks on each final runtime, including independent parsed-output and original-producer compatibility checks. |
| Packaged Explorer | 6 scope/parity checks and independent 1k/10k/50k scale checks execute against relocated model.js. Six UI assets match source/original/relocated hashes exactly. |
| Final browser | 39 functional checks; no page errors or remote requests. Actual C# saved export opens through the file picker. Screenshots are retained and visually inspected. |
| Prior evidence / native boundary | All 307 previously bound evidence references, the configured Classic archive and native quarantine record retain their original hashes. No native dispatch occurred. |

The final 50k-node/50k-edge first viewport measured 725.2 ms; the earlier 1,004.1 ms and 1,121.3 ms misses remain retained. This final measurement meets the 1,000 ms target; consistent performance across runs remains unestablished. Warm traversal and graph rendering results are separately retained; synthetic saved-model measurements do not prove whole-project/native capture completeness or actual desktop-host behavior.

Package ZIP SHA-256: `36c4af0f9504aa71568e8cad50aeb8a40e1ea91494c203936a8344b939f19e11`. The exact package is tia-mcp-offline_20261021_040431_d7189605.zip (`artifacts/pw21w5s1/artifacts/releases/tia-mcp-offline_20261021_040431_d7189605.zip`). Its original/relocated manifest and contents are verified. Exact Host/Core/UA DLL hashes are in the final verification receipt. The installed MCP package was not changed.

## Review and retained outcomes

Independent review reproduced impossible rehashed UA scalar values and the null-versus-omitted virtual endpoint mismatch. Both are repaired and covered by final tests. UA deadline cancellation and normalized plan flag validation were also repaired before the source freeze. The unchanged independent scalar probe now rejects both original mutations. HMI replays actual earlier producer records, including their refusal behavior and unchanged bytes.

The first package attempt failed the managed no-network baseline check. Its audit timing is consistent with the prior publication-failure scenario's delayed read completing after the next capture's shutdown. The old audit did not retain RequestHandle, so exact attribution of that original event remains an inference. The corrected fixture deterministically reproduces this tail with an exact request handle. Its test-only repair awaits only that request's completion before establishing the next baseline, and an unrelated real read still fails the unchanged no-network comparator. The repaired managed suite runs against the exact packaged runtime modules; the original session and capture harnesses pass 65 and 160 checks against the unchanged relocated Host. The package is strictly reverified. This is a qualified continuation, not a claim that the original package runner passed. Completion receipt (`artifacts/pw21w5s1/artifacts/package-completion-01/result.json`) and source delta (`artifacts/parallel-wave5-20261021/ua-harness-delta.json`) bind that boundary.

Original failed development attempts and both earlier viewport misses remain retained. Coordinator source-binding collection initially refused CRLF in three edited inputs; the inputs were normalized to repository LF before collection and freezing. UA retention (`artifacts/parallel-wave5-20261021/ua-lane-retention.json`), HMI retention (`artifacts/parallel-wave5-20261021/hmi-lane-retention.json`), Explorer retention (`artifacts/parallel-wave5-20261021/explorer-retention-receipt.json`) and independent scalar review (`artifacts/parallel-wave5-20261021/ua-review/review-result.json`) bind the retained inputs.

Final reporting copies and hashes are under `artifacts/parallel-wave5-20261021/final-reporting`. Original aggregate/package process receipts, logs and workflow evidence are under `artifacts/pw21w5s1/artifacts`; no failed result is relabeled as passing.

## Next work

1. Confirm consistent Explorer 50k first-viewport performance across runs; complete native collection scope and actual desktop-host acceptance separately.
2. Define and implement UA subscriptions with revised settings, Publish/Republish sequence, overflow and interruption semantics. Polling does not establish subscription or independent online program/address association.
3. Extend HMI conversion only through separately specified type/format profiles and their preservation tests. Native import, installation and runtime visuals remain unqualified.
4. Resolve the original uncaptured diagnostics-reporter lifetime with admissible retained identity/ancestry/terminal evidence before another native dispatch. Current PID absence cannot replace that evidence. Unified settings, physical tests, actual disk-full and hosted CI remain separate gates.
