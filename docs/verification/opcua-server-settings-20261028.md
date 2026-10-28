# OPC UA server settings — 28 October 2026

Task 3 of the [runtime real-server plan](../plans/runtime-real-server-plan.md): the prepared kind
`opcua_server_settings_set` turns a CPU's OPC UA server on and sets what the real-server runner needs.
Curated evidence: `local.opcua.server-settings.20261028`
([summary](../../evidence/local.opcua.server-settings.20261028.json)). PLCSIM was not used; every run
worked on an owned scratch CPU 1511-1 PN `6ES7 511-1AL03-0AB0` V4.1 in TIA Portal V21 Update 3.

## Attribute names

V21 documents no OPC UA server attribute list, so the names are pinned from native captures in
[`fixtures/opcua/server-settings-v21-1511-v41.json`](../../fixtures/opcua/server-settings-v21-1511-v41.json):

| Payload field | Native attribute | Owner | Notes |
| --- | --- | --- | --- |
| `serverEnabled` | `OpcUaServer` (Boolean) | the CPU's `OPC UA` sub-item | off on a new scratch |
| `port` | `OpcUaPort` (UInt64) | same | exists only while the server is on; default 4840 |
| `autoAcceptClientCertificates` | `OpcUaAutoAcceptAllClientCertificates` (Boolean) | same | exists only while the server is on; default false |
| `runtimeLicense` | `OpcUaPurchasedLicense` (UInt64 0..3) | the CPU | value names from the V21 hardware parameter PDF |
| `guestAccess` | none | — | refused with `UnsupportedCapability` |

With the server off, the sub-item has only `OpcUaServer`, `OpcUaClient`, the application name and URI,
`OpcUaExportArrayMembers` and `OpcUaServerCertificate`. Switching it on exposes 20 more settings
(port, session and monitored-item limits, security policies, diagnostics and `OpcUaAccessControl`) and
`OpcUaRequiredLicense` on the CPU. The fixture records them; only the four above are written.

## Findings

- **Compile needs an OPC UA user.** The first native apply (run `uq_cfbd73dd`) wrote both attributes
  and read them back, but the device compile refused: "Because the OPC UA server is activated, at
  least one user must have the 'OPC UA server access' function right." The right is the UMAC device
  function right `OPCUAServerAccess` (group `OPC UA`). `umac_role_set` grants it; no inspection lists
  available device rights, so a refused grant now names them in its details.
- **Guest access is UMAC, not an attribute.** V21 models OPC UA guest access as the anonymous UMAC
  user holding `OPCUAServerAccess`. `umac_role_set` never changes the anonymous user, so `guestAccess`
  stays refused. Task 4 needs a login user or an operator-made seed.
- **Restore runs in the applying Portal.** `change_restore` after a Portal reopen refuses with
  `SessionChanged` (run `uq_6a507103`), so the runner restores one change before the reopen and checks
  persistence with later changes.
- Enabling the server also makes TIA assign a server certificate at compile (`OpcUaServerCertificate`
  changes). No other CPU or sub-item attribute changed, and no block, type or tag table.

## Passing run

`scripts/qualify_opcua_server_settings.py` on build `a2f3b3d` (frozen `D:\wp\rs05`), 36 of 36 steps:

1. Eleven typed refusals change no attribute: unknown field, empty payload, bare value, stale
   `expected` (`ExpectedValueMismatch`), unknown license, `scopePath`, guest access, port and
   certificate acceptance while the server is off (`UnsupportedCapability`), port 0 and 65536, stale port.
2. `umac_role_set` grants `OPCUAServerAccess` to the scratch's runtime role.
3. Server on with `SimaticOpcUaS71500Small`: checkpoint, device compile with zero errors, save, native
   readback. `change_restore` in the same Portal returns every CPU and sub-item attribute to the
   pre-change state.
4. On the restored copy: server on again, then port 4840→4841 and automatic certificate acceptance on.
   Exactly those two attributes change.
5. A fresh owned Portal reopens the project with identical attributes; both journals read `applied`.

## Not covered

- Other CPUs and firmware, downloads and any OPC UA client connection. Task 4 connects to the server
  this kind configures.
- The server certificate TIA assigns at compile is not inspected; Task 4 pins it with
  `certificate_inspect` after compile.
- `runtimeLicense` records the purchased license in the project; no entitlement is verified.
