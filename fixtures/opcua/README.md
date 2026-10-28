# Offline OPC UA model fixtures

These independently authored NodeSets exercise the pinned general XML envelope and review code. They are **not Siemens exports, mapped PLC fixtures, deployable machines or native acceptance evidence**. The declared standard-model dependency is not bundled or resolved. Dates and version numbers are illustrative model metadata.

Independent expectations:

| Comparison | Required result |
| --- | --- |
| v1 against itself | No reviewed changes, no compatibility certification. |
| v1 to v1.1 | Temperature added; existing identities preserved; model metadata and references require review; zero known breaking rows. |
| v1 to v2 | Three old namespace-1 identities removed, three namespace-2 identities added; Speed changes from Float to Double and uses a new ID/BrowseName. All three declared clients affected by removed IDs. No automatic migration pairing or alias creation. |

`consumers-v1.json` binds the exact v1 bytes. `Dashboard-B` declares its browse entry and leaf dependencies as node keys; this is not automatic BrowsePath analysis. `MES-C` declares its method identity; the fixture does not implement PLC method instructions. The managed suite separately tests method-argument change detection, arrays, collisions, unresolved references, access/extension changes and stale consumer evidence.

The next native fixture must capture independent PLC declarations and the real Siemens mapping extension dialect. Do not add guessed `DB_Machine` mapping XML to these files or use their successful offline review as native import support.

## Explicit dependency set

`dependencies/device.xml` uses namespace index 2 for `urn:example:types`; `dependencies/types-v1.xml` uses index 1 for that URI. Together they resolve `Sensor → Telemetry → Reading` through a variable DataType and a structure field DataType. They are minimal authored NodeSet envelopes, not complete standard/companion models.

`types-v2.xml` changes only Reading's BrowseName plus model version metadata. The v2 consumer manifest binds the exact device and v1 type file bytes. Independent expectations: one breaking node-field change, SensorClient indirectly affected through two edges, TypeClient directly affected, UnrelatedClient with no known impact. Omitting the type file reports a missing node and required model; supplying v1 and v2 together reports ambiguity. See [offline dependency analysis](../../docs/reference/opc-ua-dependencies.md) for commands and limits.

## Native server settings

`server-settings-v21-1511-v41.json` pins the CPU OPC UA server attribute names that `opcua_server_settings_set` writes: `OpcUaServer`, and while the server is on `OpcUaPort` and `OpcUaAutoAcceptAllClientCertificates`, on the CPU's `OPC UA` sub-item, and `OpcUaPurchasedLicense` on the CPU. It holds native captures of owned scratch CPUs 1511-1 PN V4.1 (TIA V21 Update 3) with the server off and on, plus the license value names from the V21 hardware parameter description. Guest access has no attribute: V21 models it as the UMAC anonymous user's `OPCUAServerAccess` right.
