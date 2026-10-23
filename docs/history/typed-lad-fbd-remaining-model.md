# Typed graphical model and remote qualification handoff

Historical record. See [current status](../STATUS.md) and [remaining TODO](../TODO.md) for the latest checkpoint and work order.

Checkpoint: 2026-10-08, stopped after the user's request to finish the next checkpoint and leave precise TODOs. This document records the model agent's evidence and remaining work. It does not replace the overall workstream qualification report and does not claim complete LAD-010 through LAD-070 acceptance.

## Implemented model and emitter

- Versioned typed LAD power-flow and FBD dataflow models; supported Boolean primitives, branches, six comparisons, FC/FB calls, timers and counters.
- Exact resolved operand/type/scope/callee/instance inputs; pin direction, writeability, cardinality, cycle, instance ownership/sharing and target-profile checks. Unsupported combinations have explicit diagnostics.
- Partial native reader retains complete native XML and hashes, with unknown content opaque. Unsupported SCL networks remain opaque. Bounded operand edits retain unknown envelopes; full replacement requires a completely understood selected network.
- Deterministic IDs distinguish document-global hexadecimal object IDs from decimal UIds scoped to each FlgNet. Duplicate lexical aliases are rejected. Unchanged native subtrees retain their exact XML and IDs. Structural selected-network replacement records its native export ID reallocations explicitly.
- Native-observed FBD dependency ordering, numeric FC parameter ordering, and same-namespace software-unit CallInfo encoding are implemented. Qualified calls use one qualified Name, without inventing a Namespace attribute. Cross-namespace calls remain unsupported without qualification.

Implementation files:

- `src/TiaMcp.Openness.V21/TypedGraphicalModel.cs`
- `src/TiaMcp.Openness.V21/TypedGraphical.cs`
- `src/TiaMcp.Openness.V21/TypedGraphical.Validation.cs`
- `src/TiaMcp.Openness.V21/TypedGraphical.Native.cs`
- `tests/TiaMcp.Tests/TypedGraphicalCorpusTests.cs`

The last focused C# checkpoint had 24/24 groups passing, including complete reads of 79 independently exported LAD/FBD networks and 35 captured-profile pure emission checks. These are structural/model tests, not PLC runtime evidence. The main integration agent owns the final combined build and test result after subsequent integration changes.

## Native engineering evidence owned by this agent

All cases used actual MCP calls and isolated owned scratch projects. No physical controller was used. All positive cases below separately passed compilation, complete prepared/native XML comparison, changed-project save/reopen, and original-checkpoint restore/reopen. Runtime was explicitly not run.

| Evidence | Cases | Result |
| --- | --- | --- |
| `artifacts/tqx02/index.json` | `typed-create-fc-lad`, `typed-create-fb-lad`, `typed-edit-lad`, `typed-edit-fbd` | All four engineering/preservation/persistence/recovery gates passed with immutable worker 04. |
| `artifacts/tqx03/index.json` | `typed-create-fc-fbd`, `typed-create-fb-fbd` | All four gates passed with immutable worker 06 after fixing the native-observed FBD dependency ordering. |
| `artifacts/tqx02/typed-insert-lad/result.json` and `native-id-diagnosis.json` | LAD insertion | Native compilation passed with zero errors/warnings, but unrelated object IDs changed; checkpoint recovery verified the original. |
| `artifacts/tqx02/typed-insert-fbd/result.json` and `native-id-diagnosis.json` | FBD insertion | Same unrelated-ID constraint; this earlier run also exposed the FBD ordering issue subsequently fixed and positively qualified through block creation. |

`tqx01` is retained failed infrastructure evidence from the old public inventory limit. It is not positive qualification. Its interruption record explains the safe pre-mutation stop. Every subsequent owned qualifier completed cleanup; this agent has no running owned portal or qualifier at this checkpoint.

### Insertion acceptance remains unmet

Native insertion into the captured normal FB document reassigns the existing block title object IDs 8/9 to D/E while assigning the inserted network objects 8 through C. The original network and title contents are unchanged, but the user's requirement to preserve unrelated IDs is not met. Strict comparison was retained.

`NativeIdPreservationUnsupported` now rejects this shape at preparation, with the affected existing IDs and predicted renumbering. Terminal insertion without following object IDs remains supported by the pure builder, but no normally exported native terminal fixture has yet qualified that route. Do not label standard native insertion qualified, remove title/comments, or normalize away the mismatch.

- [ ] Find an independently authored native insertion target that satisfies preservation of unrelated object IDs, then execute both languages through all four engineering gates.
- [ ] If native export intrinsically prevents that preservation contract, retain the explicit unsupported diagnostic and record insertion as an unmet acceptance criterion. A user-approved requirement change would be needed to permit unrelated ID renumbering.

## Integration review findings handed to the main agent

Read-only review covered `OpennessSession.TypedGraphical.cs`, `OpennessSession.TypedInstances.cs`, `OpennessSession.TypedDependencyProfiles.cs`, the full-project fingerprint and native instance-target resolution.

Two prepare-time gaps were reported and the main agent confirmed fixes:

1. A planned `createGlobal` instance DB must not reserve the same case-insensitive name as a planned `createBlock` caller. Baseline-only name collision checks had missed this combination.
2. Nested UDT member resolution must update the declaring namespace after each named-type expansion and resolve each continued named-type path exactly. Using the original caller namespace for a nested unqualified type can select the wrong declaration.

The project fingerprint already covers changed block names/numbers and type/tag metadata; no redundant stale-reservation finding was raised. Existing global and multi-instance routes were not changed by the review itself.

- [ ] Include the confirmed resolver/collision fixes in the final immutable build and its matching test evidence. The main agent and contracts agent own the collision negative case and combined validation.
- [ ] Keep catalog/route/type restrictions synchronized with observed evidence. Pure structural success must not widen runtime or native target qualification.

## Remote deployment prerequisite and adapter

Read-only preflight used the existing `artifacts/vm-access/mcp.vm.final.json` exactly, including strict SSH host checking, batch authentication and InteractiveUser launcher selection. It did not substitute a local executable or modify the guest.

- `artifacts/typed-remote-preflight/deployment-readonly.json`: 2026-10-08 13:44:50 UTC start; configured SSH endpoint 192.168.75.129:22 timed out after approximately 15 seconds. MCP initialization never completed; SSH exited 255. No native process/project was attached or changed.
- `artifacts/typed-remote-preflight/host-prerequisites.json`: at 13:50:21 UTC, the existing VMware `vmrun list` reported zero running VMs; VMnet1 and VMnet8 were Up. This supports the guest-off diagnosis. No VM was started, and no networking, account, firewall, license or installation setting was altered.

Historical same-day VM qualification evidence remains historical. It cannot qualify the new typed implementation or establish current reachability.

The typed qualifier now supports the actual MCP configuration through `--config`, using the existing bounded deployment client. Prepared caller/dependency XML is read through `artifact_read`, with chunk boundaries and complete hash/length checks. Guest paths are never opened on the client. The configured launcher owns its external artifact root. The adapter retains transport/configuration SHA and shutdown facts, with no fallback/replay.

Adapter files:

- `scripts/qualify_typed_graphical.py` (existing local CLI/helper APIs retained)
- `scripts/typed_graphical_transport.py`
- `scripts/test_typed_graphical_transport.py`
- `docs/guides/remote-qualification.md`

Seven focused transport tests and nine existing typed qualification tests passed. The transport tests use controlled bytes; they do not establish guest/native qualification. Configured mode currently requires `--engineering-only` and fresh owned scratch. It explicitly rejects client-local baseline archives and remote simulator assumptions.

### Next remote packet

Do not run this packet until the exact existing guest is available and the final immutable release has been deployed and strictly verified using the existing deployment workflow. Use a newly generated external configuration selecting that exact release, retaining the existing strict SSH identity/configuration and InteractiveUser route. Do not reuse a configuration pointing to an older release as evidence for new code.

```powershell
python -B scripts/qualify_deployment.py `
  --config <actual-final-release-mcp-config.json> `
  --report artifacts/typed-remote-final/deployment-readonly.json `
  --timeout 330 --target-label 'Existing Windows 11 TIA V21 guest'

python -B scripts/qualify_typed_graphical.py `
  --config <actual-final-release-mcp-config.json> --engineering-only `
  --case typed-boolean-lad --case typed-boolean-fbd `
  --output artifacts/typed-remote-final/typed --timeout 330
```

Run the commands from the client copy of the exact release so its fixture corpus and qualifier match the deployed bundle. Preserve the guest deployment receipt, strict manifest verification, configuration hash, native bridge metadata, all per-case reports and relay shutdown receipts together.

- [ ] Re-establish the existing guest's availability without assuming authorization for guest/network configuration changes.
- [ ] Deploy/verify the final immutable package and regenerate its external strict configuration.
- [ ] Execute both LAD and FBD typed cases through actual remote MCP, compilation, full readback, save/reopen and checkpoint recovery.
- [ ] Keep remote engineering gates and local/remote runtime assertions distinct; remote runtime remains unqualified.

No new remote, native or build work was started after the user's checkpoint instruction.
