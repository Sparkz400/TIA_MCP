# Unrelated compiler findings: native qualification (7 November 2026)

Evidence: `local.compile-baseline.unrelated-errors.20261107`
([summary](../../evidence/local.compile-baseline.unrelated-errors.20261107.json)), issue
[#221](https://github.com/Sparkz400/TIA_MCP/issues/221). It covers the shared
[compile verification](../guides/engineering-workflows.md#compile-verification).

## Run

`scripts/qualify_unrelated_errors.py` ran on Debug builds of `5a215447` (TIA Portal
V21 Update 3) in one owned scratch project and passed 30 of 30 steps. The project
held the scratch CPU 1511-1 PN, an added CPU 1511F-1 PN, and two more standard CPUs
whose interfaces share one subnet and the address 192.0.2.10. That conflict fails
every device compile while every block stays consistent, so it stands in for the
4 November SENTRON device error.

| Step | Result |
| --- | --- |
| `safety_import_xml` of `Main_Safety_RTG1` with a comment | Applied, saved, comment read back; `compilerSucceeded: false`, 2 errors; 26 existing findings, no introduced error |
| `network_configure` of the F-CPU interface, same session | Applied without restart or admission cleanup; the conflict still existing |
| `tag_create` and an SCL consumer FC (`source_upsert`) | Applied; their PLC compile is clean |
| `network_configure` moving one conflicting CPU to 192.0.2.11 | Applied; 12 resolved findings, clean compile |
| `tag_set` turning the consumed tag into Int | `CompileFailed`, `recovery_required`, not saved; the only introduced error is the consumer's `Data type 'Int' cannot be converted implicitly into data type 'Bool'` |

## Found while qualifying

- The first runs refused the F-CPU's own `hardware_add` because adding an F-CPU
  raises new warnings. The old rule accepted warnings, so outside tag-table
  imports a change still passes on new warnings when no error was introduced.
- `network_configure` with the subnet the node is already on called
  `ConnectToSubnet` again, which TIA refuses. It now skips the reconnect.

## Not covered

- Device compiles report shifting warning identities; the counts above include
  them.
- Only the kinds listed ran. HMI kinds keep their own baseline rules, and library
  transactions use the shared comparison without a native run here.
- An inconsistent block on any PLC still stops preparation of kinds that need the
  full project fingerprint, so same-PLC block errors are not covered (#218).
- Read and export behaviour with unrelated inconsistent objects was not changed.
