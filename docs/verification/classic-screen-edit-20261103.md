# Bounded Classic screen editing — local verification, 3 November 2026

This initial record concerns the local screen-edit change before its deployment.
The subsequent recovery fix and open-project evidence are recorded
[separately](classic-screen-recovery-20261103.md). This record does not qualify a Siemens
fix for Classic `GraphicList.Export`, all Classic profiles, runtime behavior or
whole-HMI preservation. The exact contract is in the
[HMI engineering guide](../guides/hmi-engineering.md#bounded-classic-screen-editing).

## Scope and implementation

`hmi_classic_screen_edit` imports one existing, unlinked regular screen on
`classic-rt-advanced`. It compares every regular screen of the selected HMI and
binds project state plus that declared scope in its stale-change fingerprint.
It does not dispatch graphic-list export or require unrelated PLC block payload
fingerprints. Shared resources, other HMI targets, PLC and hardware content are
explicitly outside the equality claim. A whole-project checkpoint archive is
still created before import.

The target XML is verified immediately after import. Target XML and peer-screen
content are then verified after compile and after save. Error/warning descriptions, owner paths and counts
must match the baseline. The exact observed Classic timestamp/byte-count
container is treated as compile metadata; the original evidence remains intact.

## Offline checks

- Isolated Release build: zero errors; compiled-worker metadata guard confirms
  no direct Classic `GraphicList.Export` calls.
- 76 HMI request, scope, preservation, capture-failure and diagnostic tests pass.
- The retained native before/after compile pair passes the new diagnostic
  comparison: zero errors and 18 unchanged real warnings. Timestamp and byte-count
  differences caused the older comparator to refuse the otherwise matching pair.
- 313 core tests passed during this change; no TIA API calls were made by those
  tests. Eight contract tests and 17 recovery-routing tests also pass.
- Generated API bindings are refreshed without promoting qualification or release
  acceptance.

## Native copy evidence

A separate local MCP Host and worker operate only on an explicitly authorized,
login-protected disposable project copy using installed V21 Update 3 and WinCC
RT Advanced. Authentication uses a local helper with credentials passed in memory;
that helper does not import screens. All screen mutations use public MCP
`change_prepare` and `change_apply`.

The current copy differs from older candidate XML, so that XML was not applied.
The test changes only one screen's background RGB value from `242, 245, 248` to
`243, 245, 248`, starting from a fresh native export.

The first native import/readback passed, and all 67 peer screens were unchanged
in the post-compile capture. The apply stopped before its final save because the
old diagnostic comparator treated the changing compiler timestamp header as a
warning. Both compiles had zero errors and the same 18 warnings.

Checkpoint retrieval then required project authentication. Automatic
`change_restore` did not complete the protected project's reopen. A local
credential callback opened the extracted checkpoint copy; fresh MCP preparation
verified equality of all 68 recovered screen exports with the original checkpoint
scope hash. This proves authenticated recovery of the declared screen scope,
not unattended protected-project recovery or whole-project payload equality.

The final isolated build (`d836e4caae6b4804affe887bc0a5957c`) passed the public MCP
workflow on the authenticated recovered copy:

- Preparation matched all 68 checkpoint screen exports before any new edit.
- Import and immediate exact target readback passed.
- Both compiles reported zero errors and 18 unchanged warnings; warning owner
  paths were compared as well as their text and counters.
- Exact target XML and all 67 peer screens passed verification after compile and
  again after save (`hmi_saved_screen_content_verified`).
- The saved copy was closed and authenticated again. Fresh MCP preparation
  matched the saved scope hash across all 68 screen exports, with no unsaved state.
- Same-key apply replay returned the same change, fingerprint and stages without
  another write. No screen-editing run crashed TIA.
- The final test Host and worker exited normally with code zero; its owned TIA
  instance and credential helper also exited.

One fresh test-instance startup timed out during the Siemens Openness security
check before a project opened. A subsequent start succeeded after one-time access
was allowed; this was separate from the screen workflow. No permanent application
registration was performed by the test.

At the end of this initial test, the 19 changed source/project files matched the
native-tested isolated build inputs. The active connector executable, worker
binaries and configuration still matched the pre-task SHA-256 baseline.
This is local fixture evidence; profile-wide native
qualification and unattended protected-project recovery remain unverified.

Private XML, project archives, compiler records and credentials are not included
in this repository. Native evidence and build manifests are retained locally.
This initial test did not change the active connector configuration or installed
binary, or attach to the live project. Later connector deployment and recovery
testing are covered by the separate record linked above. Nothing was downloaded
to equipment.
