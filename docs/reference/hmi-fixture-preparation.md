# Prepare exact HMI prerequisite fixtures

`scripts/prepare_hmi_fixture_ui.py --prepare-only` validates original fixture,
archive and helper references and writes a new `preparation.json`.
It starts no process, acquires no native/store ownership and grants no admission.
It preserves the original reports and explicitly leaves HMI compile, fresh
reopen, bridge recovery and native qualification false.

Supported preparation profiles are `classic-comfort`, `unified-comfort` and
`unified-pc`. The source must contain exactly one selected positive owned
fixture with independently observed archive retrieval, distinct HMI owners and
an unchanged archive. A PLC recovery report is no longer required (1 November
2026): it never established HMI archive recovery or preservation, and no current
runner writes one.

```powershell
python -B scripts/prepare_hmi_fixture_ui.py --prepare-only `
  --profile classic-comfort `
  --helper <exact-built-HmiSeeder.exe> `
  --source-report <original-native-report.json> `
  --output <new-preparation-directory>
```

The packet identifies the exact archive, owners and permitted prerequisite:
configure an existing start screen for Classic, or configure the encrypted
transfer password and confirmation for Unified. It never permits disabling
security, executing project scripts, online work or runtime operations.

The user resumed prerequisite editor work on 20 October 2026. With current
explicit scope and a sequential native window available,
use the same inputs with a **new** output directory, omit `--prepare-only`,
and list every applicable current artifact store with `--known-store`.
The coordinator revalidates inputs and holds a shared per-user native dispatch
lock, resource-store locks, original helper/main process handles and an atomic
non-breakaway Windows job through helper dispatch and terminal cleanup. A preparation
packet cannot substitute for that current ownership.

The main editor is the helper's exact direct child. Its compiler and background
processes are retained separately as descendants; their presence is not a
second editor. Cleanup requires every captured process object to exit and the
original job to drain. Ephemeral observations do not claim individual handles;
the job covers ordinary inherited CreateProcess descendants. Observed processes
outside the job and missing main-editor capture refuse. Brokered or alternate-parent
creation and Siemens job compatibility remain unqualified.

The 20 October Classic attempt's missing reporter lifetime is now enforced as a
durable quarantine in `%LOCALAPPDATA%\TiaMcp\native-dispatch-guard`, shared across
source checkouts. The default path still blocks another native window, including Unified.
No new job, free store lock or current PID absence can promote that old attempt.
See [containment and quarantine evidence](../verification/native-dispatch-guard-20261020.md).
The separately reviewed `--isolate-prior-boot` route permits a new local disposable
copy after corroborated full OS reboot, while leaving that exact old ticket and
unknown cleanup intact. It retains current registry/store ownership, a fresh
atomic process job and an atomic new output directory. Every other unresolved
ticket still refuses. See [isolation conditions](native-dispatch-isolation.md) and
the [actual Unified prerequisite qualification](../verification/native-isolated-dispatch-20261021.md).

Only the newly retrieved owned fixture may be edited. Save the reviewed
prerequisite settings in its editor, then supply the exact command described by
its `native/ui-ready.json`: `capture-saved-close`, the current review SHA-256,
process ID and project path. The helper requires a saved state, captures
complete compiler diagnostics, and refuses an error or incomplete result.
It records a configured archive and exact cleanup. Fresh-process reopen and
operation-specific bridge preservation/recovery must still be verified
separately before any profile is enabled.

To abandon an editor attempt, supply `abort-close` with the same exact review,
process and project binding. This closes the owned editor without capture,
save or archive and retains a failed/aborted outcome. Unknown or mismatched
commands refuse.

The Python harness tests file-only packet behavior and post-launch cleanup
failures without starting TIA. `TiaMcp.HmiRepairAdmission.Tests` links the exact
native admission helper into portable tests, including changed archives,
unsupported profiles, unreviewed owners and unheld windows.
Neither test suite establishes successful native fixture configuration.

## Compile the configured fixture through `hmi_compile`

`scripts/qualify_engineering_gaps.py --hmi-fixture <configured.zap21>` qualifies
the production `hmi_compile` change kind on a completed preparation's archive.
The archive must be `<output>/native/configured.zap21` of a completed preparation
and match the SHA-256 its `<output>/result.json` recorded.
In an owned TIA it retrieves the archive with `project_retrieve`, checks the
refusals (an extra payload field, `compile=false`, a missing target) and the
preview scope, then compiles every HMI target through `change_prepare` and
`change_apply`: each must report `compiled`, zero errors and zero warnings in a
complete diagnostic tree, and `change_status` reads it back. `hmi_inspect`
(every section, each screen's items, the Unified typed model) must be unchanged
by the compiles. After `tia_detach` a fresh Host reopens the saved project, finds
the same `hmi_inspect` content and compiles every target again; the last compile
is restored from its checkpoint and the recovered copy compared once more.
Every inspected page and row must name the project of that step (retrieved,
reopened, recovered), and empty screens or an empty Unified model refuse.
Session-bound identity fields and Classic export hashes are not compared.

```powershell
python -u -B scripts/qualify_engineering_gaps.py --run-native --host <frozen TiaMcp.Host.exe> --worker <frozen TiaMcp.Openness.V21.exe> --artifacts D:/Entwicklung/TIA_MCP-public/artifacts/hc --hmi-fixture <preparation>/native/configured.zap21
```

`project_retrieve` refuses a destination over 80 characters, so the run
directory (`<artifacts>/eh_<8 hex>`) must stay within 56; the runner checks this
before starting TIA.

## Verify the configured saved state and archive

`scripts/qualify_hmi_configured.py` follows a completed fixture preparation with
two sequential native lifetimes. `reopen-retrieve` opens the exact saved owned
project, captures every available inventory/scalar, compiles every exact owner,
closes it, retrieves the configured archive into a fresh directory, captures and
compiles again, then saves and closes the recovered copy. `recovered-reopen`
requires that completed first result and opens the exact recovered project in a
second fresh helper. Neither phase saves the originally configured project.
Native open/compile can update project metadata; original project byte identity
is not asserted. Archive and original result bytes remain pinned.

```powershell
python -u -B scripts/qualify_hmi_configured.py --phase reopen-retrieve --helper <exact-HmiSeeder.exe> --configured-result <completed-preparation/result.json> --known-store <existing-artifact-store> --output <new-output>
python -u -B scripts/qualify_hmi_configured.py --phase recovered-reopen --helper <same-exact-HmiSeeder.exe> --configured-result <same-preparation/result.json> --prior-result <first-output/result.json> --known-store <existing-artifact-store> --known-store <first-output> --output <second-new-output>
```

The coordinator pins the helper and source results, validates the original raw
reports and terminal registry/job chain, and repeats source validation while
holding the registry/store window before dispatch and at completion. The
existing narrow post-boot isolation route still preserves the historical unknown
cleanup. New output is claimed atomically; native launch remains inside the
non-breakaway job from creation. Unknown completion is retained without replay.

The independent comparator preserves every captured scalar type, nested order,
unknown field and available export. Only top-level owner ordering and the
documented native XML export timestamp may differ. Nonfinite values, truncated
captures, missing/changed export files, duplicated owners, compiler warnings,
incomplete diagnostics and hidden error messages refuse acceptance. Explicitly
unavailable getters remain visible and do not establish content coverage.
Current Unified captures contain no native export files, so equality qualifies
only their recorded inventories/scalars. Whole-HMI preservation, production
operations, visual appearance and runtime behavior remain unqualified.

Each helper records loaded `Siemens.Engineering*` assembly identities, locations
and hashes; this does not identify every private module inside Portal. Cleanup
uses explicit SDK process disposal and the original retained handles/job.
`explicitNativeTermination=true` records that SDK action; no coordinator-forced
termination, normal-lifecycle proof or independent lock-release proof is inferred.
See the [actual configured-fixture verification](../verification/hmi-configured-recovery-20261021.md).
