# Retained Web polling captures

Six `runtime_capture_*` tools extend the [owned Web session](physical-runtime-sessions.md) with bounded acquisition, local evidence and diagnostic observation reports. This implementation needs no local Siemens installation. Acquiring physical data still needs a reachable configured controller; all current execution evidence uses a disposable loopback server.

The same tools can instead poll an owned local PLCSIM Advanced V8 instance, optionally one sample per PLC cycle; see [PLCSIM capture source](#plcsim-capture-source).

## Workflow

1. Connect an explicit protected-credential Web session and browse exact symbols with `children:false`. Retain the returned attempt and selection IDs.
2. Call `runtime_capture_start` with a fresh caller-retained 32-hex `captureId` and `request`: `attemptId`, `selectionIds`, `intervalMilliseconds`, `durationSeconds`; optionally `maximumSamples`, `maximumBytes`, `representation` (`simple` or `raw`) and a saved `reviewId`. Repeating the same ID/request observes the original intent. It never starts another worker, even after restart. A changed request is refused.
3. `runtime_capture_status` returns the last committed prefix and its `snapshotRef`. In-flight/unpublished observations can exist beyond that prefix. `runtime_capture_stop` cancels this Host's worker and retains the final prefix; it does not log out. Cancellation during a session-identity check can invalidate the session, requiring explicit disconnect and a fresh connection attempt. There is no automatic reconnect.
4. `runtime_capture_page` reads a pinned prefix with an offset and 1–200 rows. Pages fit a 240 KiB row budget. A larger row is explicitly omitted from pages and remains in export.
5. `runtime_capture_export` accepts the pinned reference and `json` or `csv`. Download each returned path through `artifact_read` and verify its returned SHA-256. CSV always has a JSON companion containing the complete plan and quality context. Spreadsheet auto-conversion of CSV numbers is not a fidelity guarantee.
6. Open the portable Project Explorer and choose **Open capture**. Load the exported JSON, filter signals, page through records and expand **Exact read JSON**. Download preserves the supplied JSON text. This is offline supplied-evidence viewing, not source authentication or native qualification.

Supplying `reviewId` alone retains a declaration and the saved review must exist with its exact content hash at start. This alone does not resolve individual engineering declarations or prove that the selected controller runs that review's executable; even an explicitly supplied unrelated review remains unverified. Reopening or repeating an existing capture uses its retained review ID without rereading that review or restarting polling. Selection metadata, connection epoch, endpoint, representation and peer-certificate hash are retained; every sample uses the existing session's permission, identity and metadata checks.

### Explicit diagnostic-case mappings

To retain resolved engineering declarations as well, supply `request.association` with `caseId` from `diagnostic_plan`, `mappings:[{selectionId,signalId,reason}]`, and optional `assertedProgramIdentity`. Supply the case's exact `reviewId`. Map every selected online signal once, in `selectionIds` order, to a distinct case signal. For example, a caller can map an explicitly browsed `"DB_Line".Conv2.Enable` selection to the case's `enable` signal, with a reason describing that choice.

Before recording capture intent, the Host rereads the hash-bound case/review, checks the source inventory and exact selected instance, and re-resolves each declaration through the diagnostic resolver. Missing/unavailable declarations, changed scope/namespace/evidence, or a datatype differing from the observed metadata are refused. Datatype comparison ignores letter case only; there is no coercion or inferred alias. The full capture plan still has the 128 KiB metadata limit; oversize evidence is refused rather than silently truncated.

This route inherits the diagnostic planner's selected-instance and resolved-scalar subset. It does not infer standalone global/tag-only associations without a resolved diagnostic instance, array/structure expansion or unresolved aliases. Those broader declaration routes remain open; unassociated read-only capture is still available.

The committed plan retains the source project, block, instance evidence, source revision and mapped declarations alongside the existing online selections. It survives removal of the original case/review and Host restart. Export/Explorer show these **caller-declared mappings**, including reasons and any asserted program label. The Host does not infer equivalent online addresses from symbolic names. `addressMatchVerified`, `programMatchVerified`, `sourceAuthenticityVerified` and `nativeQualified` all remain false; a program label is not independent executable identity evidence. These mappings do not admit behavioral assertions or writes.

The association fields are optional additions to the capture contract. Existing unassociated captures remain readable unchanged. Use the updated Host/Explorer and verify that the returned plan contains `association`: an older Host can ignore the new request field and start an unassociated capture. Earlier strict schema consumers may reject records containing the new fields. See [association verification](../verification/runtime-association-20261018.md).

## Analyze sequential observations

Call `runtime_capture_analyze` with a pinned `snapshotRef` from a capture that has an explicit diagnostic-case association, and `units` mapping every captured case signal ID to its unit. For example:

```json
{"snapshotRef":"<pinned capture reference>","units":{"enable":"1","step":"count"}}
```

Bool requires `1`; numeric prediction units must match exactly. Units are caller declarations, with no conversion. The exact associated diagnostic case must still exist; captures retaining alarm bindings also require the original saved review for source revalidation. Older captures without those bindings keep their existing behavior and do not require that review. Unassociated captures cannot be analyzed by this route. All committed rows are streamed, including captures larger than the separate trace importer's 10,000-sample limit.

The report groups sequential reads by polling cycle and evaluates the case's existing Boolean/numeric predicates. It counts matched, contradicted and unknown cycles. A false `when` condition does not apply and contributes to none of those counts. A hypothesis without a prediction has zero counts. Uncaptured signals, failed reads, provider errors, raw representation, wrong types and numeric values that cannot be represented exactly remain unknown. There is no carry-forward, interpolation or fabricated skipped-cycle observation. Each retained example includes individual read order, host times and failure reason; clock reversal flags are preserved.

Every behavioral verdict remains **unresolved**, including when all observed cycles match. Sequential reads are not simultaneous samples; caller source mappings do not establish online address/program equivalence. No native qualification, causation or fix proof is claimed. Value changes compare only consecutive observed cycles with valid values; gaps and failures are not bridged. The host read times do not timestamp PLC transitions. Examples are bounded to 20 per hypothesis and changes to 200, with full counts and explicit change truncation. A still-running pinned prefix records unknown uncommitted data; later observations do not change that report.

The returned `id` can be passed to `diagnostic_report_export` for exact JSON or readable Markdown/HTML. Reports include source declarations, connection context, request, recorded progress, uncertainty and complete source JSON. Repeating analysis of the same prefix/case/units gives the same report ID. Once retained, report export survives removal of the case/review and Host restart. Reanalysis still requires the case. These tools perform no network access. Analysis comparisons and retained JSON preserve full-width integers; consumers must use exact JSON bytes or an exact-number parser, since ordinary JavaScript number parsing can round report values. Schema: `contracts/runtime-capture-analysis.v1.schema.json`. See [analysis verification](../verification/runtime-analysis-20261018.md).

## Saved alarm operand bindings

When the selected diagnostic case has explicit [alarm bindings](alarm-executable-bindings.md),
capture start rederives them from the original saved review and requires every
selected binding to match. Altered or unresolved evidence refuses with
`RuntimeCaptureAssociationMismatch` before capture intent or polling. The plan
retains the complete report under `association.alarmBindings`; the capture may
select only a subset of its diagnostic signals. No new runtime request option is
needed. The combined plan remains limited to 128 KiB; oversized evidence refuses
with `RuntimeCaptureLimit` without truncating bindings or creating capture intent.

Status, reopen and raw capture export retain these original bindings after removal
of the diagnostic case/review. New analysis needs both originals and revalidates
the exact retained binding report. Once analysis is saved, its JSON, Markdown and
HTML exports remain available without the originals. Explorer and readable reports
distinguish **Selected for capture** from **Not selected in this capture**; selection
does not imply a successful value read. Unselected values remain unknown.

All source-authenticity, native-qualification, runtime-alarm-association and
coverage-completeness flags stay false. Saved call evidence does not prove which
invocation ran or whether an alarm was active. Old records acquire no new field
and retain their previous bytes and analysis behavior. See
[verification](../verification/runtime-alarm-bindings-20261018.md).

## Optional retained security handoff

Set `request.handoffRef` to the exact `runtime_handoff_review` reference and
`reviewId` to its saved review. Start requires consistent original declarations
and the actual owned session's host ID, connection/epoch, endpoint, certificate
pin, login subject/mode and protected credential reference. The retained plan
adds `handoff` with nested version `runtime-capture-handoff.v1`. Its session
owner is distinct from the capture owner; old plans acquire neither field.

Every bound read revalidates the original handoff, access and saved model bytes
before network dispatch. Existing session verification, fresh permission and
symbol-metadata checks still apply. Changed or missing original evidence ends
the capture with a retained `RuntimeCaptureHandoffUnavailable` read error;
context disagreement uses `RuntimeCaptureHandoffMismatch`. Reconnecting does
not authorize reuse of an earlier connection generation. Repeating an existing
capture ID observes its prior intent and never resumes polling.

Historical status, pages, capture exports and saved analysis exports remain
readable after evidence changes or Host restart. Creating a new analysis still
requires the original handoff dependencies. JSON retains the exact binding;
Explorer and Markdown/HTML reports display its separate session ownership and
unverified authority. Forged verification/admission flags are refused.

Matching declarations never prove target/program identity, effective scoped
rights, source authenticity or diagnostic assertions. Provider failures,
missing samples and gaps remain independent quality outcomes. Revalidation is
bounded synchronous file work and can contribute to skipped polling slots;
this is not a hard real-time acquisition path. See the
[integration verification](../verification/security-runtime-capture-20261018.md)
for executed checks and remaining gates.

## PLCSIM capture source

`runtime_capture_start` can read an owned local PLCSIM Advanced V8 instance through the [simulator worker](../guides/simulation.md) instead of a Web session:

```json
{"attemptId":"<creationId>","selectionIds":["<32-hex>","<32-hex>"],"intervalMilliseconds":100,"durationSeconds":60,
 "source":"plcsim","simulator":{"instanceId":3,"instanceName":"TiaMcp_<GUID>","cycleSynchronous":true,
  "signals":[{"symbol":"\"DB_Line\".Conv2.Enable","type":"bool","datatype":"Bool"},
             {"symbol":"\"DB_Line\".Conv2.Step","type":"uint16","datatype":"UInt"}]}}
```

- `attemptId` is the instance's `creationId` from `sim_create`, or the `creationId` used to adopt it with `sim_select`. On every call the worker rechecks the instance ID, name, owned storage and that creation receipt. Instances this Host does not own are refused before capture intent exists. One capture per instance.
- `selectionIds` are caller-chosen 32-hex labels for the signals, in order. Association mappings reference them.
- `type` is the typed PLCSIM read (the `sim_read` types). `datatype` is the declared PLC type and must fit it: `bool`→Bool, `int8`→SInt, `uint8`→USInt/Byte, `int16`→Int, `uint16`→UInt/Word, `int32`→DInt, `uint32`→UDInt/DWord, `int64`→LInt, `uint64`→ULInt/LWord, `float`→Real, `double`→LReal, `string`→String, `wstring`→WString. Diagnostic mappings compare this declared type; PLCSIM tag metadata is not read. A tag of another type fails the native typed read.
- `representation` must be `simple`; `handoffRef` is not accepted.

Each scheduled cycle makes one worker call that reads every signal. Each signal still gets its own row. Its `readResultJson` is a `plcsim-runtime-read.v1` record: instance, symbol, type, `status` (`read` or `readFailed`), exact value, `providerErrorCode` for a bad-quality read, worker UTC request/response times and `syncPoint`. A call-level failure (ownership, target mismatch, sync-point timeout, worker or bridge loss) becomes one explicit error row and ends the capture with `sessionUnavailable`. Bounds, gaps, stop, status, page, export and restart behave as for Web captures.

### Cycle-synchronous mode

`cycleSynchronous:true` requires a running instance. Start records the current `OperatingMode`, registers the sync-point event, switches to `SingleStep_C` and waits until the CPU holds at its next cycle end. Each sample calls `RunToNextSyncPoint`, waits up to 5 s for the sync point, then reads every signal while the CPU holds. The rows of one cycle share that `syncPoint` (count and nanoseconds since the previous sync points). The interval paces the steps, so the simulated CPU runs one cycle per sample; in `SingleStep_C` its virtual time is not frozen between steps.

Stop, a terminal failure, any capture limit and Host shutdown restore the recorded mode and unregister the event. `sim_release` without `deleteOwned` and closing the simulator worker also restore it. If the restore cannot be confirmed, for example after a lost worker, status reports `localFailure: PlcsimOperatingModeRestoreFailed` and the instance can remain in single-step mode until restored with PLCSIM tooling.

The per-row `syncPoint` is the retained evidence. Capture status, analysis and diagnostic acquisition keep `atomicAcrossSignals` and `scanSynchronized` false. The mode is compiled against the installed V8 API only and is not natively qualified.

### Diagnostics

PLCSIM captures with `request.association` work with `runtime_capture_analyze`, `diagnostic_experiment_acquire_runtime` and `diagnostic_runtime_acquisition_export` like Web captures. The analysis `capture.context` is the simulator source. Project Explorer **Open capture** accepts Web captures only.

## Limits and timing

There are at most four active workers and one capture per owned session. Each plan selects 1–64 exact symbols, a 100–60,000 ms interval, duration 1–3,600 seconds, 1–100,000 read attempts and a 1–64 MiB capture budget. Reads are sequential. Host monotonic elapsed time schedules polls; after a slow cycle, missed scheduled poll slots become explicit gap rows and the worker does not issue a catch-up burst. A poll may be cut short by duration/stop; returned errors are retained separately from provider results.

Storage uses a synchronous buffer of at most 128 rows and less than 1 MiB, with no acquisition queue. Storage delay therefore contributes to observed skipped slots. This is the smaller bounded implementation of the plan's proposed 4,096-sample/8-MiB channel. Publication occurs at most 128 rows/segment or roughly one second of observed elapsed time. There is no unbounded queue or eviction. The store reserves the whole requested budget and limits retained data/exports to 512 MiB and 64 capture records; conservative reservations can refuse work before physical usage reaches that ceiling. Disk writes depend on the operating system and do not have a real-time latency guarantee.

Read results remain JSON **strings** in records so UInt64/Int64 and arbitrary structured values survive MCP and JavaScript without floating-point conversion. The nested read contract preserves provider error status, host UTC start/end and monotonic request duration. Gaps, failed values, known host-dropped results and unknown changes between polls remain separate. No zero, interpolation, source timestamp, atomic multi-signal snapshot or scan synchronization is invented.

Duration is checked against monotonic elapsed time before each selected-signal read as well as through cancellation. Each timer completion is a wake-up, not permission to read: an early callback causes another positive wait until the scheduled slot or duration boundary. Slots scheduled at or beyond the duration never start reads. A delayed timer callback cannot authorize another read after the duration. Already-started I/O may complete late, and its actual timing is retained; this is not a hard real-time deadline. A late wake-up or cancellation during a wait retains missed scheduled slots through the stop time, capped before the duration boundary. UTC clock changes do not alter scheduling. See [early-timer regression evidence](../verification/runtime-early-timer-20261020.md) and [deterministic scheduling and OS access-denial evidence](../verification/runtime-scheduling-20261018.md).

## Recovery and integrity

An immutable plan and hash-linked immutable segments are published through a small atomic head file. A snapshot reference pins a verified prefix, including after subsequent acquisition. Reopen validates the chain, plan/selection context, record ordering, result contract and budgets. Export rechecks segment hashes. Local path guards reject links and aliases; the store has one exclusive owner.

Unpublished temporary/orphan segments are never adopted. After a Host crash, a running prefix reopens as `interrupted`, with uncommitted data/loss explicitly unknown. No polling/login is resumed. Publication failure retains the previously committed prefix; a local storage failure is exposed while the owner remains alive. A record directory interrupted before head creation may be unavailable rather than repairable. Hashes prove consistency with a retained reference; a local attacker replacing all data and references is outside source-authentication claims. There is no rollback-resistant trust anchor.

## Evidence and remaining work

See [capture verification](../verification/runtime-captures-20261017.md). The fixture in `fixtures/physical-runtime/capture-portable.json` comes from the loopback MCP harness, not a controller. Schema: `contracts/runtime-capture.v1.schema.json`.

The [review and publication-failure checks](../verification/runtime-capture-faults-20261018.md) cover malformed/missing/changed review refusal, declared association after review removal/restart, Windows head-replacement sharing contention, changed-head refusal and worker-level segment collision. Explicit mappings retain re-resolved engineering declarations; diagnostic observation reports evaluate supported predicates without admitting behavioral assertions. Deterministic clock/worker tests and an actual isolated Windows access-denial test now pass.

The [pressure qualification](../verification/runtime-pressure-20261020.md) adds 32,768 mixed read attempts across 64 signals, independent exact-value/schedule/gap checks, eight waves of four saturated workers, reservation exhaustion and healthy subsequent captures. These use the production poller and store with a manual clock and test session; they prove application limits and recovery under those bounded conditions. They do not prove exhausted OS memory/handles, a long wall-clock soak or physical controller timing.

The separate opt-in [real-clock soak](runtime-soak-qualification.md) exercises four concurrent captures under bounded touched-memory, OS-handle and hashing load, followed by cancellation, an owned process crash and fresh-process recovery. Its independent original-byte oracle checks actual timing, gaps, resource observations and unchanged committed prefixes. The retained 60-second results do not establish system-wide exhaustion, long-term leak freedom or real-device behavior.

Actual disk-full remains an RT-03 prerequisite gate. `scripts/Test-RuntimeCaptureDiskFull.ps1` owns a new fixed 64 MiB VHD and admits filling only that bounded fixture; it retains the VHD and external evidence and dismounts only the exact owned disk after its test process finishes. On this workstation creation succeeds but mounting fails with Windows `0x80070522` (required privilege missing). No volume was mounted or filled, and the actual-full test body is unqualified until run in an appropriately privileged environment. Independent address/program association and assertion admission remain RT-07 work. The existing numeric/Boolean trace importer is intentionally not used to flatten arbitrary runtime JSON or provider errors. Physical timing/CPU profiles, program association, UA subscriptions, diagnostic service routes and write/control qualification remain separate open packages.
