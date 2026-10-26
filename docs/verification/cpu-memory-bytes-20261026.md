# CPU system and clock memory bytes — 26 October 2026

Closes DMC's remaining prerequisite: enable the system and clock memory bytes on each consuming CPU
([vendor review](vendor-intake-review-20261025.md#prerequisites-and-remaining-qualification)).

## Route

`change.hardware_cpu_memory_bytes` (`change_prepare` kind `hardware_cpu_memory_bytes`) writes four native V21
attributes of one CPU device item: `SystemMemoryByte`, `ClockMemoryByte` (Boolean) and `SystemMemoryByteAddress`,
`ClockMemoryByteAddress` (UInt64 byte numbers).

```json
{"hardwarePath": "<CPU device item>", "attributes": {"SystemMemoryByte": true, "SystemMemoryByteAddress": 1,
 "ClockMemoryByte": true, "ClockMemoryByteAddress": 0}}
```

- Openness declares a byte number only while its flag is enabled. Enabling a flag therefore requires its byte
  number, a byte number of a disabled flag is refused, and the worker writes flags before byte numbers.
- Refused before any write: items without both flags (`UnsupportedCapability`), other attributes
  (`UnsupportedAttribute`), non-Boolean flags, byte numbers outside 0–65535, unchanged requests (`InvalidRequest`)
  and both enabled bytes on the same byte (`AddressConflict`). Siemens checks the CPU's own bit-memory size at compile.
- The same checkpoint, compile and configured-hardware preservation as the other hardware kinds; the manifest
  includes the four settings, and disabling a flag removes its byte number from the expectation.

## Discovery

A read-only Openness probe (own Portal, unsaved temporary project) listed the attributes before and after enabling:

| CPU | Flags | Byte numbers |
| --- | --- | --- |
| CPU 1511-1 PN `6ES7 511-1AL03-0AB0/V4.1` | both, disabled | appear when enabled, defaults 1 and 0 |
| CPU 1516-3 PN/DP `6ES7 516-3AP03-0AB0/V4.1` | both, disabled | appear when enabled, defaults 1 and 0 |
| CPU 1214C DC/DC/DC `6ES7 214-1AG40-0XB0/V4.7` | none | none |

## Native qualification

[`local.cpu_memory_bytes.20261026`](../../evidence/local.cpu_memory_bytes.20261026.json) (`scripts/qualify_cpu_memory_bytes.py` at 65aa278, build 60afdc8 frozen at `D:\cmq\b2`, owned scratch, 51/51 steps):

- **Every S7-1500 CPU in the project:** the scratch CPU 1511-1 PN and an added CPU 1516-3 PN/DP. On each, system
  memory at MB20 and clock memory at MB21 are written, compiled, preservation-verified and read back.
- **TIA's tags:** enabling adds 14 tags to the default tag table (`System_Byte`, `FirstScan`, `DiagStatusUpdate`,
  `AlwaysTRUE`, `AlwaysFALSE`, `Clock_Byte`, `Clock_10Hz` … `Clock_0.5Hz`) at the selected bytes. A function reading
  `"FirstScan" OR "Clock_1Hz"` compiles in both PLCs. Disabling the bytes again leaves these tags in place.
- **Save and reopen:** a fresh owned Portal reopens the saved project with identical settings, blocks, types, tag
  tables and tags.
- **Recovery:** moving the bytes to DMC's documented defaults (MB1/MB0) and restoring the checkpoint returns the
  exact prior state. Clock memory byte 65535 passes the bridge and the write, then fails compilation
  ("Value outside the permitted range: [0..16383]"); the change is `recovery_required` and its checkpoint restores it.
- **Restore:** the consumer stops reading the bits, then both CPUs return to their original disabled settings.
- **Refusals:** the eight bridge refusals above, each without a block, type or tag-table change.
- **Boundary:** an added CPU 1214C DC/DC/DC is refused with `UnsupportedCapability`.

## Not covered

- DMC FB compilation itself: the DMC Siemens Open Library archive is no longer on this machine. The
  [tag-table prerequisite](../../evidence/local.library_dmc_prerequisites.20261025.json) and this route together
  cover DMC's documented CPU setup.
- S7-1200 and other CPU families, other firmware, F-CPUs and software-unit scopes.
- Runtime behavior of the memory bits, and removing TIA's tags when the bytes are disabled.
