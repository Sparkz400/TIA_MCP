# Diagnostic watch authoring (WT-04)

The production Host exposes `watch_table_generate` and `watch_table_prepare_authoring`. Both produce reviewable artifacts without changing the native project. Their prepared operations use the existing `watch_table_apply`, fresh-reopen, recovery and reconciliation tools and the same durable ownership store.

**New authoring mutations remain disabled in normal Hosts until their native lifecycle profiles are independently verified.** The isolated qualification Host can exercise the complete implementation. Managed tests are evidence of adapter behavior, not native qualification. The earlier accepted existing-row Bool literal profile remains separate.

Prepared reviews now include `exactProfileCandidate`. Its only candidate key is `watch.create-owned-v21-1511-v41-de-active-bool.v1`: the recorded V21 installed API fingerprint, CPU 1511-1 PN V4.1, owned scratch root `WT05_BoolCreation`, direct `TiaMcp_EnableGate_DB.Active : Bool`, only `de-DE`, the packaged template, one default reason comment, exact native ID mapping, and the complete recorded six-peer/hardware/access profile. It compares complete candidate XML, including IDs and metadata. Only the generated owned project name is substituted in native peer identity paths for the profile fingerprint; leaf text, addresses, peer bytes, language settings and access assignments remain significant. The review retains the actual worker digest. `scopeMatched` describes this bounded candidate, while `nativeVerified` and `productionApplyEnabled` remain false. A historical binary receipt does not admit a new build, and none of the generic create/add/ID keys is enabled.

## Generate from an exact instance

Call `watch_table_generate` with `projectPath`, canonical `plcPath`, exact FB `blockPath`, exact `instanceDbPath`, `tableName`, and optionally `maxRows` (default 64, maximum 256). The V21 worker observes the saved offline project, reuses the existing native instance/type resolver and declaration reader, and exports the FB and instance DB. It refuses an ambiguous, protected, wrong-type or incomplete source.

The first native capture profile covers direct root instances with ordinary symbolic identifiers. It traverses captured structured declarations; arrays, embedded multi-instances, aliases and unsupported selectors remain explicit unresolved suggestions. Software-unit/namespaced instance selectors require another native capture profile. The wider pure `WatchTablePlanner` API remains available with its existing captured diagnostic, call, network and I/O inputs; those inputs are not silently treated as native observations.

The dispatcher runs `WatchTablePlanner`, retaining selection reasons, datatype, exact source/storage references, unresolved suggestions and budget omissions. The response includes `generationId`, `generationSha256` and the complete plan. Source captures and the generated plan are immutable operation inputs. Generation never claims a live address or observes runtime values.

## Review creation or maintenance

`watch_table_prepare_authoring` accepts the [authoring request contract](../../contracts/watch-table-authoring.v1.schema.json). For example, after generation returned a candidate named `member0`:

```json
{
  "generationId": "<returned ID>",
  "generationSha256": "<returned digest>",
  "idempotencyKey": "diagnostic-watch-review-1",
  "mode": "create",
  "destination": {
    "projectPath": "D:\\OwnedFixture\\Fixture.ap21",
    "plcPath": "<exact captured PLC path>",
    "group": "root",
    "tableName": "MotorDiagnostics"
  },
  "template": {"group": "root", "tableName": "ExistingNativeWatch"},
  "steps": [
    {"kind": "add", "candidateId": "member0", "templateOrdinal": 0,
     "comments": {"de-DE": "Freigabestatus", "en-GB": "Enable state"}}
  ]
}
```

The table name must match generation. Creation requires an independently observed absent destination. If `template` is omitted, creation uses the packaged, hash-pinned genuine V21 owned native row capture; this permits authoring the first table in a project. An existing table in the same PLC can instead supply native row grammar through the optional exact `template` selector. An update defaults to its own captured rows, or the packaged template if the target is empty. The native adapter checks watch and force names across the selected PLC, rechecks under its exclusive lease, and imports creation with `ImportOptions.None`. It never creates folders or replaces a collision.

Steps are sequential and zero-based: `add` selects a generated candidate and a template row, `remove` selects a current ordinal, `reorder` supplies a complete current permutation, and `comment` supplies exact culture/text pairs. Template ordinals always refer to the captured pre-edit template. `insertAt` defaults to the end. At most 128 steps and 256 rows are admitted, with a 16 MiB complete-review limit.

Added rows clone captured native grammar, receive the exact captured symbolic selector and an explicit scalar display format, clear the stored modification literal, and retain a diagnostic reason by default. Supported scalar types follow the planner's explicit scalar set; each datatype has its own native admission profile. New rows use only active project cultures; existing rows retain every unselected language. Comments preserve whitespace, Unicode and line breaks. New language items clone captured item grammar and require a culture already active in the project. No project language setting changes.

The review retains the complete native before snapshot, template, original generation, every step's before/after rows, final XML and its digest, required native profiles, and an explicit native-ID remapping where structural edits require it. Structural editing only admits the bounded reference-free signal/comment-row grammar, with a separate preservation gate for comment rows. Unknown reference-bearing or opaque grammar is refused for that transformation; the ordinary preserving editor still retains opaque data for supported existing-field edits. It never normalizes away unexpected native readback differences, including disappearing blank comment rows.

## Apply, reopen and recover

Apply takes the existing `operationId` and exact `reviewedPlanSha256`. Every required mutation profile must have accepted native evidence before normal production admission. The review currently reports those gates as pending. An isolated qualification bypass is controlled by Host startup options and cannot be supplied in an authoring request.

Source identity and content are reacquired at preparation and under exclusive access before checkpoint and import. Source declarations must also agree with the destination's complete preservation snapshot. A changed FB, instance, destination, peer, worker epoch, binary or target profile invalidates dispatch. Capture receipt IDs may change; source contents may not.

The durable adapter reserves the original resource, records checkpoint and dispatch intent, retains the returned readback before validating it, checks saved typed/XML readback and peers, then awaits a different native process/session for persistence verification. No native mutation is automatically replayed after interruption.

Creation has a hash-bound absence receipt for the exact destination, rather than a fabricated empty watch document. Recovery restores the pre-create archive into a separately reserved location and verifies that the table is absent there. It then requires a fresh recovered reopen and a separate original-project observation. Lost import/retrieval responses use the existing evidence-only reconciliation paths; production recovery remains available even while new mutation profiles are gated.

Monitoring, online modification, forcing, folder management, arbitrary table deletion and access-rule changes are outside this slice. No compilation or runtime success is inferred from table consistency.

## Native qualification harness (WT-05)

`scripts/qualify_watch_authoring.py` exercises the actual stdio Host tools in order: generate, prepare authoring, independent file-only review, apply, saved fresh reopen, checkpoint retrieval, fresh recovered reopen and original-resource reconciliation. Its initial case creates `WT05_BoolCreation` with exactly the captured `TiaMcp_EnableGate_DB.Active : Bool` output and the default diagnostic reason. It uses the packaged native template without seeding a watch table in the project. Every run requires a new short artifact root and frozen Host/worker packages:

```powershell
python -u -B scripts/qualify_watch_authoring.py --host <frozen-host.exe> --worker <frozen-worker.exe> --root D:/TIAW/<new-run>
```

The package ancestor must retain a `manifest.json` or `source-manifest.json` with SHA-256 references to the frozen build sources. The runner retains its controller source, complete package inventories, tool responses, review, journal, archive and exact original process-handle exit proofs. It does not attach to an existing TIA session. A rejected independent review prevents apply; a failed native readback remains failed while the retained checkpoint can be explicitly recovered. Apply is never replayed.

`scripts/verify_watch_authoring.py <run-root> --output <new-result.json>` independently rechecks retained evidence without starting TIA. It checks source FB/instance declarations against their native peer exports, full candidate XML and explicit ID remapping, absence before creation/restoration, typed/XML readback, unchanged peer bytes, separate process/session epochs, journal ownership and terminal process proofs. Tests are in `test_verify_watch_authoring.py` and `test_qualify_watch_authoring.py`.

This initial case does not qualify additions to an existing table, removals, reordering, explicit multilingual comment edits, other datatypes, collision or stale-source refusal. A passing creation receipt must not enable those broader profile keys. See the [WT-05 trial report](../verification/wt05-authoring-20261016.md) for the exact native result and remaining production admission work.

The corrected packet in `qualify_watch_exact_profile.py`, with independent `verify_watch_exact_profile.py`, passed on frozen package-03 at `D:/TIAW/we1602`. It adds a saved temporary FB HeaderAuthor change to exercise the native pre-effect stale-source guard, finally-restoration and fresh restored source observation, three actual Host collision refusals (exact name, case-folded watch name and force name), and stale-epoch refusal of a pending operation before checkpoint. Full creation/reopen/distinct recovery/fresh recovered reopen and terminal ownership checks passed. Its qualification-only helper writes its immutable pre-effect baseline beneath the Host artifact root and archives before any source write. A failed/lost helper response can be reconciled only by an independently fresh complete original baseline; a changed or unknown outcome stays owned.

Production creation remains disabled. The creation-specific lost-Host-reply controller and 8 portable tests are delivered, but its native packet was not dispatched. The exact production admission decision and final normal Host proof remain pending. Actual Host prepare against a concurrently changed source and wider authoring profiles are also unqualified. See the [current implementation, retained failures, and exact packet record](../verification/wt06-exact-authoring-20261016.md).
