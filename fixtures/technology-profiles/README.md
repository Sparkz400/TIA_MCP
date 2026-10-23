# Opt-in S7-1500 technology profiles

These profiles target exactly `OrderNumber:6ES7 511-1AL03-0AB0/V4.1`, the CPU identifier used by the repository's scratch hardware fixture. The original external-encoder profile is retained as a disconnected negative dependency fixture. The separate connected recipe below supplies a catalog-backed hardware partner. Do not substitute another CPU implicitly.

| Profile | Configuration intent |
|---|---|
| `cpu1511-fw41-positioning-axis-v10.json` | Version 10.0 positioning axis, virtual operation and linear motion type. |
| `cpu1511-fw41-external-encoder-v10.json` | Version 10.0 external encoder, incremental encoder type and first sensor interface. No hardware connection is invented. |

Siemens' V21 [Motion Control version matrix](https://docs.tia.siemens.cloud/r/de-de/v21/s7-1500-motion-control-einsetzen-s7-1500-s7-1500t/s7-1500/1500t-motion-control-uberblick-s7-1500-s7-1500t/versionen-einsetzen-s7-1500-s7-1500t/versionsubersicht-s7-1500-s7-1500t) allows technology versions 7.0 through 10.0 on firmware 4.1 and lists positioning axes and external encoders for 10.0. Its article-number restrictions retain 7.0 as the minimum for the selected `*03-0AB0` CPU. Only one Motion Control technology version can be used on a CPU.

The [Openness technology overview](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/overview-of-technology-objects-and-versions) supplies the exact `TO_PositioningAxis` and `TO_ExternalEncoder` identifiers. The [creation API](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/creating-technology-object?contentId=1HYopnU2cpLhvrB6d_0UkA) uses these type names with an explicit version and rejects unsupported combinations. This is a documented compatibility basis; the installed native create/compile result remains decisive.

The [Axis and Encoder parameter table](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/parameters-of-s7-1500-motion-control/axis-and-encoder) explicitly permits the profile values: virtual mode 1, linear motion type 0, incremental encoder type 0 and interface number 1. It also identifies `_Actor.Interface.Telegram`, `_Sensor[1].Interface.Telegram` for the selected standard CPU axis, and `_Sensor.Interface.Telegram` for an external encoder. Those hardware-dependent telegram parameters are deliberately not assigned by these profiles. Actual access metadata and converted native scalar types must be checked before writing.

`dependencies` and `expectedHardwareConnections` are omitted: these profiles do not prescribe extra native defaults or an installed hardware partner. The run below captured disconnected native connection records; that observation is not a general default contract for other CPU/type/version combinations. Initial profile parameters are configured before the first required compile. Parameter and connection readbacks must remain available when compilation fails.

Siemens' [encoder connection API](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/connecting-encoders) requires a suitable PROFIdrive telegram 81/83 partner or an existing DB-member connection. It exposes `EncoderHardwareConnectionProvider.SensorInterface`, its connection flag, connected modules/channel, logical addresses and DB-member path. The installed V21 `Siemens.Engineering.Step7.xml` also contains these public API members. The external-encoder profile does not establish any partner, so successful creation alone does not establish a compilable or hardware-connected encoder. There is no guessed encoder virtual mode.

## Recorded native evidence

`artifacts/technology-families-expanded.json` records the following on a fresh worker-owned scratch project using the exact CPU above and technology version 10.0:

| Profile | Qualified result and limits |
|---|---|
| Positioning axis | `TO_PositioningAxis` creation with `VirtualAxis.Mode = 1` and `_Properties.MotionType = 0` compiled with zero errors and one missing technological-alarm-text-list warning. Native readback returned those exact values as writable `System.UInt32` and `System.Int32` scalars. Both post-creation inspections enumerated all 242 parameters without truncation; XML export succeeded. Delete compiled with zero errors/warnings and absence was verified. This qualifies an offline virtual linear axis only. |
| External encoder | `TO_ExternalEncoder` creation with `Sensor.Type = 0` and `Sensor.Interface.Number = 1` retained exact writable native readbacks (`System.Int32` and `System.UInt32`) but failed compilation with three errors: encoder-telegram input address unset, output address unset, and mismatched input/output telegram types. Native encoder connection readback was disconnected with both addresses `-1`. The worker retained failed-state evidence and successfully retrieved the pre-change checkpoint. This is negative dependency, parameter-readback and recovery evidence; it is not a compilable encoder qualification. |

The axis actor and sensor were disconnected, and all reported hardware-connection input/output addresses were `-1`. Both full axis inspections occurred after initialized creation; they do not establish a separate parameter-edit roundtrip. Neither profile qualifies a connected drive/encoder, physical motion, runtime behavior, or another CPU/version. The combined report remains `completed: false` because of the encoder compile failure, even though axis checks and encoder recovery succeeded. See [the verification record](../../docs/verification/hardware-to-verification.md#motion-technology-object-qualification) for change IDs and recovery artifacts.

Run these profiles only through the scratch qualification workflow. A native failure must retain the failed status and recovery evidence; do not weaken checks to report a pass.

## Connected external encoder recipe

[`../encoder-hardware/cpu1511-fw41-tm-posinput2-v10.json`](../encoder-hardware/cpu1511-fw41-tm-posinput2-v10.json) is consumed by `scripts/qualify_encoder_hardware.py`, rather than by the generic scalar-only technology profile loader. It specifies the same CPU and `TO_ExternalEncoder` 10.0, plus the installed catalog module `OrderNumber:6ES7 551-1AB01-0AB0/V2.0` (TM PosInput 2), native technology channel 0 with I/O type `Complex`, and the symbolic `OperatingMode.MotionControl` prerequisite. Native discovery found the channel's writable operating-mode attribute as `System.UInt64`; the fixture seeder resolves the public enum and verifies its converted readback. It never enters guessed telegram addresses.

Siemens documents [TM PosInput 1/2 configuration for Motion Control](https://docs.industrial-operations-x.siemens.cloud/r/en-us/ax/simatic-motioncontrol-native-docs/10.0.9/hidden-links/tm-posinput-1/tm-posinput-2?contentId=u~CC4GQ~rmpZpabiKjrJVA). The channel must operate in position detection for Motion Control, and the encoder is linked through the native `EncoderHardwareConnectionProvider.SensorInterface.Connect(channel)` API. The recipe requires incremental sensor type 0, interface 1, native telegram 83 and offline automatic encoder data adaptation. Native create/connect/compile results remain decisive.

Build `scripts/native/EncoderSeeder/EncoderSeeder.csproj`, then run:

```powershell
python scripts/qualify_encoder_hardware.py `
  --host <verified-host.exe> --worker <matching-worker.exe> `
  --configuration fixtures/encoder-hardware/cpu1511-fw41-tm-posinput2-v10.json `
  --report artifacts/encoder-qualification-new-run.json
```

Without `--configuration`, the script only discovers the exact module/channel metadata in a new owned scratch. With the recipe, the guarded seeder archives that scratch, sets the observed hardware prerequisite, connects the encoder and compiles the containing device. Native TO defaults are read with their access metadata; unchanged read-only defaults are not force-written. MCP then changes `Sensor.InverseDirection` from `false` to `true`, retains its checkpoint and compilation result, and compares all native parameter metadata, the exact channel/module connection, and the complete native XML during recovery and fresh-process save/reopen checks. Only export provenance and serializer indentation are excluded from XML equality. The native seeder prepares the fixture; this does not claim a general MCP encoder-connection write route or physical encoder behavior.

The 2026-10-08 connected-fixture evidence is deliberately retained in separate records:

| Record | Meaning |
|---|---|
| `artifacts/engineering-gap-encoder.json` | Failed initial seeder attempt to rewrite a native read-only TO parameter. This remains `completed: false`. |
| `artifacts/engineering-gap-encoder-requalified.json` | Connected TM PosInput channel, telegram 83, exact scalar/connection readback, device compile with zero errors and one CPU-display-password warning, MCP parameter edit with zero errors, and checkpoint recovery with exact full native XML and native metadata. First immediate reopen encountered Siemens' project-lock release delay; the report remains `completed: false`. |
| `artifacts/engineering-gap-encoder-preservation.json` | Read-only continuation bound to the preceding report and native export SHA-256 hashes. Verifies the original owned PID exited, then independently reopens the retained recovered and changed copies in fresh owned processes. Qualification requires `completed: true` and both copy checks passed. No mutation is replayed and no lock file is removed. |
| `artifacts/engineering-gap-encoder-native-edit-audit.json` | Independent proof that the complete native XML changes only by adding `StartValue=true` under the unique Boolean `Sensor.InverseDirection` member, after excluding export provenance. |

To finish saved-copy checks after a native lock-release delay, use `scripts/verify_encoder_preservation.py --evidence <retained-report.json> --host <verified-host.exe> --worker <matching-worker.exe> --report <fresh-audit.json>`. The failed/probe records must remain intact; successful companion evidence does not rewrite their status.

## Portable qualification-oracle repair - 17 October 2026

An explicitly supplied existing technology name must be valid and nonblank; it never
falls back to a generated fixture name.

The existing scalar fixture harness now requires complete, unfiltered and mutually
consistent `engineering_read`/`technology_read` observations, stable project/process/
TO identity, known write access and exact declared parameter types. It rejects
missing/duplicate/added parameters, unintended parameter/access/type changes,
connection drift, read errors and incomplete post-delete absence. Integer/Boolean
values are distinct; numeric requests compare exactly after explicit native Single
rounding where applicable. Generic relative float tolerances no longer qualify drift.

All returned parameter records and connections must remain unchanged except for the
requested scalar values. A scalar change with intended connection side effects needs
a separately qualified complete-effect recipe; this harness does not authorize it.
Returned display observations still do not prove full native XML, excluded values,
save/reopen or runtime preservation. Native XML and connected-fixture companion audits
remain separate. A failed observation retains the failed row and does not trigger an
automatic delete/retry; later explicit recovery uses the existing native workflow.

[Verification](../../docs/verification/technology-oracle-20261017.md) covers portable
mock regressions only. Historical native results are preserved and have not been
re-run or newly certified against these stricter checks. The [full D13 plan](../../docs/plans/expansion-plans/motion-and-technology-objects.md)
keeps all remaining families, connection services and native gates open.
