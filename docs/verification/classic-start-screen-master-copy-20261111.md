# Classic start screen without the editor — native probe, 11 November 2026

A newly created Classic HMI has no start screen, and its compile aborts with
`A start screen has not been configured.` Openness exposes no setter for it, so every
compilable Classic fixture so far needed one step in the TIA editor
([fixture preparation](../reference/hmi-fixture-preparation.md)). This record shows a
route that needs none and uses only the public Openness API.

**Finding:** a screen created from a master copy in an HMI that has no screen yet becomes
that HMI's start screen. The same screen imported from XML does not.

## Probe

`HmiSeeder.exe startscreen-probe <new-output> [catalog type name]`
([source](../../scripts/native/HmiSeeder/StartScreenProbe.cs)) creates a scratch project
in an owned headless Portal with two HMI devices of the selected type. No existing
project is opened. Installed: TIA Portal V21 with WinCC Basic/Comfort/Advanced.

| Step | WinCC RT Advanced 17.0.0.0 | TP700 Comfort 17.0.0.0 |
|---|---|---|
| HMI A: two screens imported from XML, compile | aborted, 1 error: no start screen | same |
| HMI A: read and write six start-screen attribute names on the HMI software, the device and every device item | every call refused: not supported by the type | not evaluated separately |
| HMI B: no screens, compile | aborted, 1 error: no start screen | same |
| HMI B: `Screens.CreateFrom(masterCopy)` of HMI A's screen, compile | finished, 0 errors, 0 warnings, with generation | same |
| Save, archive, close, reopen, compile HMI B | finished, 0 errors; answered from the last compile | same |

The master copy is made in the same run with
`ProjectLibrary.MasterCopyFolder.MasterCopies.Create(screen)` from the imported screen,
so the route starts from an empty project. The self-description of the HMI software
lists only `Author` and `Name`; no device item describes a runtime setting.

Not shown: whether a paste into an HMI that already has screens changes the start
screen, which screen wins when several are pasted, and any profile other than the two
above. Nothing here is runtime or visual verification. The reports and the archived
fixtures stay in local artifacts.

## Compile sequence on the fixture

`HmiSeeder.exe compile-experiment <fixture.zap21> <new-output> <steps>` retrieves a copy
of the probe's archived fixture and runs the given steps in order. Two runs on the RT
Advanced fixture replayed the order of native calls of a `hmi_classic_screen_edit`
apply, to test whether the checkpoint archive leads to
`Cannot compile. Cannot access the project path.`
(the failure of 11 November 2026 that pull request 294 of this repository records):

| Steps under one exclusive access | Compile results |
|---|---|
| save, **archive**, compile, re-import, compile, re-import, compile | answered from the last compile; finished 0/0 with generation; finished 0/0 |
| save, compile, re-import, compile, re-import, compile | the same three results |

Each re-import exports the screen, changes its `BackColor` and imports it with
`Override`. The failure did not occur in either run. The fixture has one screen with one
rectangle and one tag; it has no faceplate instances, graphic lists or connections, and
the Portal ran without user interface and for under a minute. So the runs rule out the
bare sequence on a minimal HMI and nothing else.
