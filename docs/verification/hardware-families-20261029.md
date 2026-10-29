# Hardware families: S7-1200 and ET 200MP — 29 October 2026

The X10 hardware kinds were qualified with two CPU 1511-1 PN V4.1 only (`local.hardware_changes.20261028`,
[record](engineering-kinds-20261028.md)). The [hardware runner](../../scripts/qualify_hardware_changes.py) now takes
two more families through the same gates (apply, preserve, reopen, restore, refuse), with no worker or Host change.

- **S7-1200.** A CPU 1214C DC/DC/DC V4.7 (`6ES7 214-1AG40-0XB0`) is added with `configureScratchSecurity`, joins subnet
  `HwNet` and gets two SM 1221 DI16x24VDC V1.0 (`6ES7 221-1BH30-0XB0`). The first is replaced by its successor
  article `6ES7 221-1BH32-0XB0` V2.0, which keeps the addresses, and back. Port_1 links to the scratch CPU's Port_1.
- **ET 200MP on a second controller.** An IM 155-5 PN ST V4.1 (`6ES7 155-5AA01-0AB0`) is added, joins `HwNet` and
  is assigned to a new IO system of the added CPU 1511-1 PN (`network_io_assign` with that CPU's `plcPath`). Two DI
  16x24VDC HF V1.0 follow; the first is replaced by V2.0 and back. The IM's Port_1 links to that CPU's Port_1.

The links form a line (CPU 1214C – scratch CPU – second CPU – ET 200MP), never a ring. Each rack loses only its last
module, and phase B restores one change of each kind per family.

## Run

| Rows | Runner | Build | Result | Evidence |
| --- | --- | --- | --- | --- |
| `change.hardware_add`, `_attributes`, `_replace`, `_port_connect`, `_port_disconnect`, `_delete`; `change.hardware_plug`, `change.network_configure`, `change.network_io_assign` | [`qualify_hardware_changes.py`](../../scripts/qualify_hardware_changes.py) | `artifacts/x4` (`556cee5`) | Passed, 276/276 steps | `local.hardware_changes.families.20261029` |

One owned TIA Portal V21 Update 3 instance and one owned scratch project, built and run from a frozen `git archive`
copy; nothing touched a user project, a PLC or PLCSIM. The S7-1500 gates of `local.hardware_changes.20261028` ran
again in the same project. Per kind, across the three families:

| Kind | Applied | Restored | Refused |
| --- | --- | --- | --- |
| `hardware_add` | 4 (CPU 1511-1 PN twice, CPU 1214C, IM 155-5 PN ST) | 1 | 2 |
| `hardware_attributes` | 6 | 3 | 4 |
| `hardware_replace` | 6 | 3 | 3 |
| `hardware_port_connect` | 5 | 1 | 3 |
| `hardware_port_disconnect` | 4 | 3 | 1 |
| `hardware_delete` | 6 | 3 | 2 |

`hardware_plug` (6), `network_configure` (4) and `network_io_assign` (1) ran as fixture changes through the same
gates. Every applied kind persisted across a fresh owned Portal reopen. The only native side effects were the ones the
28 October run recorded: the module comment mirrored to its built-in submodule, and the ports' cable defaults on link changes.
A key-discovery pass without key expectations ran first (`artifacts/q/xd`, not evidence) and fixed the expected keys.

## Findings

- **An unassigned IO device fails the device compile.** A new IM 155-5 PN ST, and every later change while it is
  unassigned, compiles with "An attempt was made to compile a submodule that is not assigned" and "One or more
  required addresses are missing". The station's `hardware_add` and `network_configure` therefore apply with
  checkpoint but without compile and save (`deferred` in the runner); `network_io_assign` compiles and saves them.
- **Node names differ per family.** `network_configure` needs the exact native node name: `X1` on the CPU 1511-1 PN,
  `X1 : PN(LAN)` on the CPU 1214C and `IE1` on the IM 155-5 PN ST (`hardware_inspect` `network.nodes`). `X1` on the
  CPU 1214C is refused with `NodeNotFound`.
- **The catalog offers two CPU 1214C DC/DC/DC lines.** `6ES7 214-1AG40-0XB0` (V4.0–V4.7) and the G2
  `6ES7 214-1AH50-0XB0` (V1.0, V4.1) share the type name, so a "newest firmware of this type name" selection picks the
  G2. The runner names `6ES7 214-1AG40-0XB0/V4.7` exactly.
- **`network_io_assign` echoes `addressControllerOwnerPath`.** The worker adds the controller's CPU item to the
  reviewed payload, so the preview payload is the request plus that path.
- **What the assignment changes.** The controller's PLC gains the system constants `Local~HwPeerIo` (the IO system)
  and `HwMpHead~Head`, `~IODevice`, `~Proxy`, `~PROFINET_interface` and `~PROFINET_interface~Port_1/2`; the
  station's modules are named after the head item (`HwMpHead~MpDi`), not the device. Every item of the head reads
  `SharedDeviceAccessType` `PLC` instead of `Unknown`, and the IM interface gains `NumberOfExternalIoControllers`,
  `OptionalIoDevice` and `PrioritizedStartup`. Nothing else changes.
- **The IO device's connector times follow its modules.** With no module the connector reads `PnUpdateTime`
  128 ms and `PnWatchdogTime` 384 ms; the first DI 16x24VDC HF makes them 2 ms and 6 ms, and deleting the last
  module restores them. Both survive a fresh open.
- **The controller's cyclic IO bandwidth is not saved.** `CalculatedBandwidthForCyclicIoData` of the controller
  interface reads 7040 after the compile and 0 after a fresh open, as the [I-device run](gsd-transfer-areas-20261028.md)
  found; it was the only difference across the reopen, so the runner no longer compares it.

- **One `hardware_inspect` no longer covers the project.** Its `maxItems` (at most 2000) budgets items, addresses
  and attribute values together; with four devices the whole-project call came back truncated. The runner now
  inspects each device item that `device_inventory` lists, one call per item.

## X10 online rows

`device_online_browse` and `device_identify` cannot run offline or against anything available on this PC without
PLCSIM. They need, together:

- a reachable S7-1500 V4.1 or later (or a PLCSIM Advanced instance in TCP/IP mode) with the web server on, loaded
  with the project whose `device_inventory` snapshot is used; the endpoint IP must be an interface under that CPU;
- `Api.Browse` advertising `Modules.Browse` and `Modules.ReadIdentificationMaintenance` (not yet observed on PLCSIM);
- a web server certificate that chains to a root trusted by Windows for the Host's user: this route keeps default TLS
  validation and has no pin, unlike `runtime_web_*`. Trusting the certificate is a security setting for the operator;
- `read_diagnostics` for the session user, and with web authentication a token from an externally established
  `Api.Login` in a `TIA_INVENTORY_<target>` record;
- for `device_identify`, modules that answer I&M0.

The runtime real-server TCP/IP window provides the first item. A pinned route like `runtime_web_*` would remove the
trust-store step, but it is a security change and not made here.

## Still open

- ET 200SP on a second controller (its generated server-module contract is captured for the scratch CPU only),
  shared devices and I-devices under a higher-level controller.
- Address-changing replacements and `acceptLoss`; rings with media redundancy.
- HMI, drive and GSD devices; S7-1200 G2 (`6ES7 214-1AH50`) and other CPU lines.
- The X10 online rows above; physical runtime.
