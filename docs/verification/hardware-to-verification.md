# Hardware, technology-object and XML verification

The latest [connected-encoder qualification](engineering-gap-qualification.md#connected-encoder-dependencies) supplies a validated TM PosInput 2/channel/telegram recipe, exact native readback, compilation, checkpoint recovery and fresh saved-copy reopen evidence. The earlier disconnected encoder failure below remains retained as negative evidence.

Validated on 2026-10-08 through the built stdio MCP host and the V21 worker. All engineering writes used a new worker-owned scratch project or its checkpoint-retrieved copy. No user project was attached and no physical PLC or runtime operation was used.

The CPU was the installed-catalog `OrderNumber:6ES7 511-1AL03-0AB0/V4.1` (CPU 1511-1 PN). Scratch provisioning protected its confidential configuration and created a password-authenticated runtime user with standard full access scoped to the new device. Generated passwords were retained with Windows CurrentUser DPAPI and were not returned in reports.

| Fixture | Native evidence |
|---|---|
| Central digital input module | Catalog-selected `OrderNumber:6ES7 521-7BH00-0AB0/V1.0`, DI 16x24VDC HS, passed `CanPlugNew` and plug into slot 2. Inventory exposed configured I/O addresses. |
| Module attribute and delete | `Name` changed from `TiaMcp_DI_Fixture` to `TiaMcp_DI_Fixture_Renamed`, verified in inventory, then deleted and verified absent. Each mutation compiled with zero errors. |
| PID creation | `PID_Compact` 3.0 was created and compiled with zero errors/warnings. The older documented 2.3 sample was rejected by this CPU; its checkpoint was restored successfully. |
| PID parameter | Existing `Config.InputUpperLimit` changed from 120.0 to 120.125, compiled and read back exactly. This is an offline start/configuration value. |
| PID export/delete | Native XML exported, object deleted, compilation passed, then absence verified. |
| UDT XML | A two-member PLC data type was generated from SCL, exported, re-imported through `plc_type_import_xml`, compiled and read again. |
| Tag-table XML | The existing default scratch tag table was exported, re-imported, compiled and read again. This covers native table roundtrip, not arbitrary tag payload construction. |

Hardware compilation retained one warning that the new CPU's display had no password protection. Its confidential configuration and runtime user setup passed compilation. No display or anonymous-access protection was disabled as a workaround.

An initial attempted write to `Retain.CtrlParams.Gain` returned Siemens' read-only error and triggered successful checkpoint recovery. In the final compiled-source run, native metadata identified it as read-only and `change_prepare` rejected it with `ReadOnlyParameter` before applying or creating a checkpoint. The subsequent writable parameter change passed exact read-back verification. [Siemens' parameter-writing example](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/writing-parameters-of-technology-object) identifies `Config.InputUpperLimit` as a writable example.

Evidence files:

- `artifacts/hardware-to-smoke-first.json`: hardware plug/address/attribute/delete results and 2.3 rejection/recovery.
- `artifacts/hardware-to-smoke-second.json`: PID 3.0 creation and read-only parameter rejection/recovery.
- `artifacts/hardware-to-smoke-third.json`: writable parameter, export/delete, UDT and tag-table roundtrips; completed successfully.
- `artifacts/hardware-to-smoke-final.json`: final compiled-source verification of PID creation, native read-only metadata/early rejection, writable parameter/read-back, export/delete, UDT and tag-table roundtrips; completed successfully.

Run `python scripts/smoke_hardware_to.py` for a fresh full fixture. It defaults to PID 3.0 and supports separate hardware/technology skips or reopening an exact repository scratch project. These results qualify the listed CPU/module/TO combination. They do not establish general motion-axis configuration, every TO version, every hardware attribute, safety I/O behavior, or controller tuning correctness.

## Expanded central I/O qualification

The 2026-10-08 Release run recorded in `artifacts/hardware-families-expanded.json` completed successfully on a fresh worker-owned CPU 1511-1 PN, `OrderNumber:6ES7 511-1AL03-0AB0/V4.1`. It selected each module from the installed catalog, plugged the selected module and a peer, rejected an intentional address conflict during prepare, relocated and restored the selected address, deleted the peer, renamed the module, and deleted it. All 28 hardware changes compiled with zero errors. Each hardware compile retained the CPU-display-password warning described above.

| Family | Exact installed type identifier | Native channels | Native range | Verified address sequence | Rejected conflicting byte start |
|---|---|---:|---|---|---:|
| DI | `OrderNumber:6ES7 521-7BH00-0AB0/V1.0` — DI 16x24VDC HS | 16 digital inputs | 16 bits / 2 bytes | `0 → 32 → 0` | 2 |
| DO | `OrderNumber:6ES7 522-1BH10-0AA0/V1.1` — DQ 16x24VDC/0.5A BA | 16 digital outputs | 16 bits / 2 bytes | `0 → 32 → 0` | 2 |
| AI | `OrderNumber:6ES7 531-7QF00-0AB0/V1.0` — AI 8xU/I/R/RTD BA | 8 analog inputs | 128 bits / 16 bytes | `0 → 48 → 0` | 16 |
| AO | `OrderNumber:6ES7 532-5ND00-0AB0/V1.1` — AQ 4xU/I HF | 4 analog outputs | 64 bits / 8 bytes | `0 → 32 → 0` | 8 |

Each expected rejection returned `AddressConflict`; no conflicting write was applied. The native `Address.Length` values above are bit counts, while `StartAddress` is a byte offset. Inventory and inspection preserve the native `length` and add `lengthUnit: "bits"` and the rounded-up `byteSpan`. Collision checks use byte spans, and debugging-reference matches use exact bit ranges. The first expanded run exposed the rejection of valid address zero; its failed evidence remains in `artifacts/hardware-families-expanded-first.json`. The preceding worker named-pipe connection failure remains in `artifacts/hardware-families-expanded-sandbox-blocked.json`.

The apply route also verifies every address row on the selected device item, permitting only the requested start-address change. This handles Siemens' documented caveat that an address write can implicitly alter the opposite I/O type: such collateral changes fail verification and require checkpoint recovery. The four qualified modules did not produce this collateral behavior. The safeguard has pure regression coverage; this run does not qualify a coupled-address module. [Siemens address-write restrictions](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-devices-networks-and-connections/accessing-attributes-of-an-address-object).

Native channel numbers, types and exposed parameters were retained for every selected module. **Channel-parameter writes remain blocked/unqualified for all four fixtures:** no scalar channel parameter on the selected module was writable through the bridge's exposed allowlist. Distributed I/O relationships, physical signals and runtime behavior were not exercised. Technology-object scenarios were skipped in this run; the existing UDT and default tag-table XML roundtrips passed again.

The report records `completed: true`, `hardwareCompleted: true`, no unexpected failures, successful detach and host exit code 0. No failed apply or checkpoint recovery occurred in this successful run. Reproduce against an explicit matching host and worker build:

```powershell
python scripts/smoke_hardware_to.py --host src/TiaMcp.Host/bin/Release/net10.0-windows/TiaMcp.Host.exe --worker src/TiaMcp.Openness.V21/bin/Release/net48/TiaMcp.Openness.V21.exe --skip-technology --report artifacts/hardware-families-expanded.json
```

Choose a new report filename for subsequent runs to preserve this evidence.

## Motion technology-object qualification

`artifacts/technology-families-expanded.json` records the 2026-10-08 Release run on a fresh worker-owned CPU 1511-1 PN, `OrderNumber:6ES7 511-1AL03-0AB0/V4.1`, using the explicit [version 10.0 profiles](../../fixtures/technology-profiles/README.md). Initial parameters were applied within `to_create` before its required compile.

| Fixture | Native result |
|---|---|
| Offline virtual linear axis | `TO_PositioningAxis` 10.0, `VirtualAxis.Mode = 1`, `_Properties.MotionType = 0`: creation compiled with zero errors and one warning that the technological alarm text list does not exist and the device must be compiled. Native parameter readback confirmed both values, their writable access, and scalar types `System.UInt32`/`System.Int32`. Both post-creation inspections enumerated all 242 parameters with `scopeComplete: true` and no truncation, and native XML export succeeded. Delete compiled with zero errors/warnings and absence was verified. |
| External encoder dependency rejection | `TO_ExternalEncoder` 10.0, `Sensor.Type = 0`, `Sensor.Interface.Number = 1`: creation failed compilation with exactly three errors (encoder-telegram input address unset, output address unset, and mismatched input/output telegram types) and the same one alarm-text-list warning. Failed-apply evidence retained exact writable native readbacks of both supplied values as `System.Int32`/`System.UInt32`, plus a disconnected encoder connection with input/output addresses `-1`. Checkpoint recovery succeeded. |

The axis create change is `bad503a45b3a48829594214495af4aff`; its delete change is `b6dd91b01c904e32970e02f7ff49b9ae`. The axis actor and sensor were disconnected; all reported connection roles had input/output addresses `-1`. This qualifies initialized offline virtual-axis create, compile, parameter/connection inspection, XML export and delete. Both full inspections were after initialized creation, so this run does not establish a separate axis parameter-edit roundtrip or XML identity between exports. It does not qualify connected motion hardware, physical operation, or other CPU/version combinations.

The encoder change `0410d74384984eab84186edeca3972a1` retained `CompileFailed` with `state: recovery_required`. Its original checkpoint is `artifacts/changes/change-0410d74384984eab84186edeca3972a1/before.zap21` (SHA-256 `247c87c2796533f7817ea18b7a81b7707707e7e48fdfd02bac6786e524bb330f`); the same directory contains `journal.json` and failed-state archive `before-recovery-2153f103ab7848b38a9051f0f0177e39.zap21`. Recovery returned `state: restored` and opened the checkpoint under `artifacts/recovery/r_ba3a0157/TiaMcpScratch_20261008_075955_495e44ad`. The report contains no independent post-recovery XML comparison, so this evidence establishes checkpoint retrieval rather than that additional comparison. The encoder remains unqualified for successful compilation or a hardware connection.

Across the technology run there were six apply attempts: three successful PID changes, two successful axis changes, and the one failed encoder creation followed by one successful recovery. The overall report correctly remains `completed: false`; it records successful detach and host exit code 0. No connected axis or encoder was exercised.
