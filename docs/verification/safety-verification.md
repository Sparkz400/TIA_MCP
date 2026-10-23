# Offline safety verification

The latest [selected engineering-gap qualification](engineering-gap-qualification.md#safety-fingerprint-correction) passed the corrected fingerprint route with a real comment change, guarded recovery, recompilation and fresh-process reopen of both saved copies. Its native report and strict export audit supersede the earlier blocked rerun status below; the historical failures remain retained.

On 8 October 2026, the built MCP host completed an unchanged native F-block XML
round trip in a dedicated, task-created TIA Portal V21 Update 2 project. The test
used the installed **CPU 1511F-1 PN**, catalog identifier
`OrderNumber:6ES7 511-1FL03-0AB0/V4.1`. It did not attach to a customer project,
connect to a physical PLC, perform a download, or execute a safety login.

The machine-readable proof is
safety-roundtrip-verified.json (`artifacts/safety-roundtrip-verified.json`).
The [runner](../../scripts/smoke_safety_direct.py) uses the actual stdio MCP tools
and detaches its owned TIA connection in a `finally` block.

## Verified results

| Operation | Observed result |
|---|---|
| Create the F-CPU scratch and compile its software | Safety and standard software compiled with 0 errors and 0 warnings; project saved. |
| `safety_inspect` | One native F-runtime group, four program signatures and exposed safety settings; no warnings or truncation. |
| `software_inspect` | Twelve F-language blocks, including user and generated blocks. Enumeration does not imply that every block is exportable or editable. |
| Selected user-block inspection | `Main_Safety_RTG1`, FB1, `F_FBD`, consistent and unprotected; native per-block signature available. |
| `block_read` | Native user F-block XML exported and SHA-256 recorded. |
| Standard source targeting that F-block | `source_upsert` preparation rejected with `SafetyBoundary`; a second export confirmed unchanged native object XML. |
| `safety_import_xml` preparation | Accepted the unchanged exported user block; captured backup, preview and pre-change safety metadata. |
| Apply | Created a recovery archive, imported once, compiled with 0 errors and 0 warnings, and saved. |
| Post-apply verification | Native XML object payload and all observed program/block signatures remained identical; original export file hash remained intact. |
| Cleanup | Explicit `tia_detach` succeeded and the MCP host exited. |

The verified change ID is `ce7754ae3e3840ee8b075766b452703d`. Its retained
recovery archive (`artifacts/changes/change-ce7754ae3e3840ee8b075766b452703d/before.zap21`)
and journal (`artifacts/changes/change-ce7754ae3e3840ee8b075766b452703d/journal.json`)
record the operation.

| Native signature | Before | After |
|---|---:|---:|
| CollectiveOfflineSignature | 2584179645 | 2584179645 |
| SoftwareOfflineSignature | 2584179644 | 2584179644 |
| HardwareOfflineSignature | 1 | 1 |
| CommunicationAddressOfflineSignature | 0 | 0 |
| BlockOfflineSignature | 4200226891 | 4200226891 |

The native object XML payload hash was
`bc20629291de104683f55e0421f6e84106cfbebaf55554625a07704a2de79df6`
before and after. Whole exported files have different hashes because Siemens
adds `DocumentInfo.Created` provenance. The runner removes only the top-level
`DocumentInfo` element for the object comparison and separately verifies that
the original raw export remains byte-for-byte intact.

## Scope and limitations

This proves one unchanged **user F_FBD FB** import on the specified installed
CPU and software. It does not prove changed safety logic, F_LAD or F_DB imports,
protected/passworded projects, software-unit mutations, F-I/O configuration,
safety commissioning, acceptance, runtime behavior, or arbitrary hardware.
The dedicated import path accepts eligible `F_LAD`, `F_FBD` and `F_DB` blocks
conditionally; generated F-system blocks and standard/safety conversion are
excluded. Native Siemens protection and license requirements still apply.

The scratch had no safety offline password and was not logged in. Native
`SafetyAdministration` is obtained from the owning PLC `DeviceItem` through
its engineering parent chain. It is not a service on `PlcSoftware` in this
tested V21 configuration.

The first creation attempt saved and compiled successfully, then encountered
Siemens' prohibition on exporting generated `FOB_RTG1`. Its
creation report (`artifacts/safety-direct-smoke.json`) preserves that partial
result. The test reopened that exact saved scratch for the successful
read inspection (`artifacts/safety-direct-read.json`) and final round trip.

The round-trip report also exposed a reporting defect: `change_apply.safetyAfter`
had been captured immediately after import and contained temporary zero software
signatures. The independent post-compile `safety_inspect` results above are the
final observations. Source now refreshes `safetyAfter` after compilation/save;
that reporting correction was made after this recorded run.

## Comment-change failure and separate checkpoint retrieval

The later expanded safety run (`artifacts/safety-expanded.json`) used a fresh
scratch with the same CPU profile and changed one existing user F-block comment.
Native import, compilation with **0 errors and 0 warnings**, and project save
completed. The subsequent project fingerprint failed because Siemens rejected
`GetFingerprints` on protected generated compiler instance DB `DB1_C` as
inconsistent. This was a fingerprint failure after mutation, not a passed
comment-change qualification.

The retained journal (`artifacts/changes/change-3cd5bd1d42c6468e92c934ef6a9bc9ad/journal.json`)
has state `recovery_required` and `afterFingerprint: null`. `change_restore`
correctly rejected automatic recovery with `StaleRevision`, since there was no
recorded post-change state against which to check for subsequent edits. The
failed project and journal were preserved; the restore guard was not bypassed.

A separate checkpoint retrieval (`artifacts/safety-expanded-checkpoint-retrieval.json`)
then passed in a new empty worker-owned TIA instance. `project_retrieve` verified
the original archive SHA-256
`ebaa73af2ca2f0f667c462826854fe8c39a1f92991c14d5492a205fd07b92757`
and retrieved it into a new directory. The recovered original F-block XML payload
hash was `bc20629291de104683f55e0421f6e84106cfbebaf55554625a07704a2de79df6`;
both program and block signature collections exactly matched the pre-change
observations. The report records `completed: true` and host exit code 0. This
qualifies separate archive recovery while retaining the failed state; it does
not turn the failed `change_restore` attempt into a pass.

The source correction distinguishes protected native system-group blocks from
editable user blocks. With usable owning-PLC safety signatures, generated
payload checksums are explicitly excluded; identity, modification, protection
and consistency metadata and the owning safety signatures/runtime groups remain
in the fingerprint. Preview and journal coverage name the excluded paths and
mark coverage incomplete. A block name such as `DB1_C` alone never authorizes an
exclusion, and user/editable fingerprint errors still fail closed.

The corrected source built successfully and passed **82/82 C# regression tests**,
including the generated/user-block fingerprint distinction. Fresh native
qualification remains unavailable: the
isolated post-fix run (`artifacts/safety-expanded-isolated-start-timeout.json`)
stopped at `tia_start` after approximately 300 seconds with
`TimeoutOutcomeUnknown`, before any scratch creation or mutation request. The
report records `completed: false` and host exit code 0. Subsequent native
discovery identified the owned headless process with no open project, and that
exact empty process was cleaned up. The startup timeout does not establish an
authorization failure or any other specific cause, and does not qualify or
invalidate the fingerprint correction. The original compile result and separate
successful checkpoint retrieval above retain their narrower evidence scope.

Reading or preserving a signature, and a successful compile, do not establish
functional safety or constitute acceptance of a machine safety program.

The final established Release-path retry is retained in `artifacts/safety-expanded-final.json` with host and worker hashes. Like the isolated-path retry described below, it returned `TimeoutOutcomeUnknown` from `tia_start` before any project creation or mutation request. Native discovery confirmed that the recorded test instances were headless with no project before their helpers and empty instances were cleaned up. The startup cause was not established. This retry does not qualify the corrected fingerprint route, safety-unit edits or F-I/O configuration.
