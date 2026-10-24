# Fresh PLC component installation and runtime — 24 October 2026

A freshly authored FB/UDT component installed into a fresh consumer with exact
FB/UDT import, instance DB, standalone FBD FC, call and bindings. It then passed
full compile, complete preservation, save and fresh reopen, first-checkpoint
recovery and native PLCSIM runtime behavior. Evidence:
`evidence/local.plc_component.install.20261024.json` and
`evidence/local.plc_component.runtime.20261024.json`.

## What ran

`qualify_plc_component.py --run-native` in `author_new_source` mode (`c09`) on
CPU 1511-1 PN V4.1 with TIA V21 Update 2:

- An owned de-DE scratch with simulation-capable compilation authors
  `TiaMcpComponentState` and `TiaMcpComponentGate` and releases 1.0.0. It
  publishes and archives them. Native GUIDs are observed, never fabricated.
  The closure is exactly FB → UDT.
- A fresh consumer imports that closure and creates instance DB
  `TiaMcpComponentGate_1`: first available DB2, `AutoNumber=false`, initialized
  from the captured storage. It also creates the standalone FBD FC
  `TiaMcpComponentCaller`, with the Enable/Active tag bindings checked by the
  independent call/wiring/direction oracle.
- Complete unrelated-content preservation, a fresh reopen with exact
  instance-DB payload, retrieval of the **first import checkpoint** to a new
  location equal to the baseline, a recovered reopen, and component window
  completion.

An earlier pass (`c08`) used the same source without simulation support.

`component_runtime.py --run-native` then runs on the saved `c09` project (final
attempt `r05`, which continues `r04` and `r03` by recorded hash):

1. The project is proven equal to the installation's fresh-reopen snapshot.
2. Simulation-capable compilation is enabled as its own change. It leaves the
   installed XML unchanged.
3. The OB1 `Main` call is a **separate** reviewed `source_upsert` that may change
   only OB1.
4. The corpus card and credential provisioning sequence runs.
5. Toggling `ComponentEnable` true/false/true/false drives `ComponentActive` on
   every edge.

## Fixes this required

- **Captured storage profile:** it was pinned to native04's archive and GUIDs.
  Any other archived source is now eligible only as the exact committed FB → UDT
  closure. Identities are bound from the reviewed plan, and storage by complete
  installed FB/UDT content equality.
- **Siemens API check:** it no longer requires one machine's install path.
  Directory, name, version and bytes stay exact.
- **Project language:** component-qualification Hosts create scratch projects in
  de-DE only. On an English TIA, the de-DE fixtures otherwise fail to import.
- **VCI identity walk:** it ends at the Portal root or a revisited object. The
  `Parent` chain never reached null here. This is the same error that blocked
  the Test Suite qualification on 16 October.
- **Open project files:** the component window hashes saved project files with
  shared read, because TIA keeps the open `.ap21` with write access.
- **Simulation support:** the source is authored with simulation support.
  PLCSIM cannot simulate a non-editable library block compiled without it.
- **Runtime harness:** it reads the SCL OB callee from `Instance/Component`,
  provisions the scratch CPU credential, and loads the simulator tag list first.

## Still open

- **Existing-caller installation:** V21 renumbers the following block-title IDs
  on network insertion, and the typed route refuses that
  (`NativeIdPreservationUnsupported`). Accepting it needs a preservation-policy
  decision.
- **native04/native05 artifacts:** they are not on this machine, so source reuse
  and the native05 recovery audit were not run.
- **Production combined apply:** `library_integrate` apply stays disabled. Only
  this one component layout is qualified.
