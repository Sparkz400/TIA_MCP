# Retained runtime acquisition for a diagnostic experiment

`diagnostic_experiment_acquire_runtime` binds one existing immutable runtime capture snapshot to one exact saved diagnostic experiment. It performs local evidence review and publication only. It does not connect, start polling, configure a trace, stimulate a signal, or change a PLC.

This is a separate workflow from `trace_read`, `diagnostic_experiment_review` and repeated before/after measurement comparison. Sequential polling rows are never converted into simultaneous trace samples. Even when every observed pattern matches, all behavioral verdicts remain `unresolved`.

## Workflow

1. Retain a diagnostic case with `diagnostic_plan`, explicitly selecting the saved review, FB/call/instance and justified signals. Compose its watch/manual-trace experiment with `diagnostic_experiment_plan`.
2. When separately starting a runtime capture, supply its existing `association:{caseId,mappings:[{selectionId,signalId,reason}]}` with the exact diagnostic case ID and review ID. Selection IDs identify retained runtime symbol selections, or the caller-chosen signal labels of a [PLCSIM capture](runtime-captures.md#plcsim-capture-source), whose rows also retain per-row sync-point evidence. The mapping is a declaration of intended association; matching names/types and caller-supplied program labels do not establish online program or address identity.
3. Select an immutable `snapshotRef` returned by the capture workflow. The reference binds the plan and complete committed segment prefix. An active prefix remains that same prefix after later append, stop or Host restart.
4. Call:

   ```json
   {
     "experimentId": "<diagnostic_experiment_plan.id>",
     "snapshotRef": "capture:<capture-id>:<segment-count>:<chain-sha256>",
     "request": {
       "reason": "Retain this selected observation window for the saved conveyor experiment.",
       "units": {"enable": "1", "step": "count"}
     }
   }
   ```

   `units` must cover every captured diagnostic signal exactly. Bool requires `1`; numeric prediction units must match. There is no unit conversion. Unknown request fields, alternative mappings, and identity/quality attestations refuse.
5. Export the returned acquisition `id` with `diagnostic_runtime_acquisition_export`:

   ```json
   {"acquisitionId": "<acquisition.id>", "format": "html"}
   ```

   Formats are `json`, `markdown`, and `html`. Download the returned hashed artifact using `artifact_read`. JSON is the exact original acquisition record. Readable formats include the complete original JSON, chain hashes, selected mappings, per-read timings, quality/gaps, terminal limits, and unresolved observation counts.

A capture without the exact preexisting association cannot be retroactively attached by this tool. A replan requires a capture associated with the new exact case. Acquisition does not infer instance equivalence or reuse a previous instance's mapping.

## Evidence and interpretation

The acquisition rederives the experiment, watch plan, trace specification, selected saved source/instance, any Test Suite finding and alarm bindings, then resolves the capture association again. It validates the immutable runtime chain and reruns the existing sequential runtime analysis. The retained `analysisId` identifies the same `runtime-capture-analysis.v1` bytes produced by `runtime_capture_analyze`.

The acquisition contains:

- The exact retained experiment and its diagnostic case.
- The immutable snapshot reference, plan hash, final chain hash, segment count and each original plan/segment file's relative path, SHA-256, byte length and base64-encoded original bytes.
- Every committed decoded row, preserving the exact `readResultJson` string, selection, sequence/cycle, scheduled/elapsed time, observed UTC, request-start/response-received times, host-clock-backwards flag, errors, provider status and gap rows.
- Original plan selections, association declarations, requested limits and recorded progress, including terminal reason, skipped polls, dropped results and whether uncommitted data remains unknown.
- Per-hypothesis observed matched/contradicted/unknown cycle counts, bounded examples, consecutive-cycle changes, missing/unsupported values and uncaptured signal IDs.

Polling cycles group sequential reads for descriptive counting. They do not establish simultaneous values or scan synchronization. There is no carry-forward, interpolation, resampling, invented transition timestamp, or reconstruction of skipped cycles. Unsupported/raw/nonrepresentable values remain unknown. Results retain useful counts while `assertionsAdmitted`, atomicity, scan synchronization, online program/address verification, clock synchronization, source authenticity, causation, successful-fix and native-qualification flags remain false.

`completeCommittedPrefixRetained:true` means every row of the selected committed prefix is retained. It does not mean the requested capture duration completed, all signals were observed, or the PLC behavior is known between polls. A `running` prefix explicitly retains `uncommittedDataUnknown:true`; byte/sample limits, stopped/interrupted captures and missing observations remain visible.

## Bounds, reopening and compatibility

Requests are bounded to 64 KiB. An acquisition accepts at most 10,000 complete rows and 3 MiB of original plan/segment bytes, and its complete serialized document must fit 8 MiB. Larger inputs refuse with `ArtifactCapacity`; they are not silently truncated. The existing runtime export remains available for larger captures. Analysis examples are bounded to 20 per hypothesis and 200 observed changes; readable raw-row previews show at most 64 rows. The full original rows remain in the acquisition JSON. Readable exports also have an 8 MiB limit and may require the exact JSON format for larger records.

Existing diagnostic retention remains 256 MiB. Invalid requests, wrong associations, altered sources and per-document bounds are checked before publication. A later global-quota or filesystem failure may leave a valid content-addressed analysis child; no completed acquisition receipt is returned. There is no eviction or automatic cleanup.

Every export reopens the original saved source and capture chain and recomputes the analysis. A missing/changed original, changed child analysis, or honestly rehashed derived record that differs from those originals refuses. Embedded source copies are evidence, not a replacement for a missing original. Original hashes prove retained byte identity, not authenticity of supplied evidence or the running device.

The old measurement, single/repeated experiment and report contracts remain unchanged. In particular, legacy runtime analysis/export can retain its prior behavior where the old route did not require a saved review; the new acquisition route requires the original experiment source evidence. This stronger new prerequisite does not rewrite old records or upgrade them.

Contracts: [acquisition](../../contracts/diagnostic-runtime-acquisition.v1.schema.json), [export](../../contracts/diagnostic-runtime-acquisition-export.v1.schema.json).

## Offline validation

The diagnostic managed suite emits `runtimeAcquisitionFixture` in its optional samples file. This is an explicitly synthetic producer fixture, not physical PLC evidence.

```powershell
python scripts/qualify_runtime_diagnostic_acquisition_offline.py --host <Host.exe> --output <fresh-directory> --samples <diagnostic-samples.json>
node scripts/qualify_runtime_diagnostic_acquisition_browser.mjs <MCP-result.json> <fresh-browser-directory>
python scripts/qualify_runtime_diagnostic_acquisition_prior.py --host <Host.exe> --runtime-source <actual-old-runtime-session-mcp> --experiment-source <actual-old-diagnostic-repeat-mcp> --output <fresh-prior-directory>
```

The first command uses the actual Host/tools and an independent original-chain decoder with negative controls. The browser command checks actual downloaded HTML and rendered Markdown, exact embedded JSON, hostile text, narrow/desktop layout and keyboard access. The prior command uses actual previous-producer records and original receipt/binary hashes; it does not construct new records and label them historical.
