# Explorer relationship providers and collection scope — 2026-10-26

This record covers offline implementation and tests only. No TIA Portal or
PLCSIM process was started; the native step below is prepared but not run.
Nothing here is native, desktop-host or release qualification.

## Implemented

Relationship providers, each a Siemens-free adapter in
`src/TiaMcp.Openness.V21/ProjectModelRelationshipProviders.cs` over an existing
native read:

| Provider | Edge kinds | Native source | Resolution rule |
| --- | --- | --- | --- |
| `configuredNetwork` | `connectedSubnet`, `ioSystemController`, `ioSystemDevice` | Network-interface section of `hardware_inspect` (`InspectConfiguredNetwork`) | Exact captured subnet/IO-system path and native controller/connector owner. A controller row owned by another interface stays unresolved. Controller- and device-side observations share one edge identity. |
| `technologyConnections` | `technologyConnection` | `technology_read` hardware connections with the reference renderer | Only hardware/channel references the native reader resolved to an exact hardware path. Tag, block and unresolved references keep their provider identity and stay unresolved. |
| `softwareUnitRelations` | `unitRelation` | `PlcUnitBase.Relations`, captured as unit metadata | V21 relations are names. A target resolves only when the hierarchy capture is complete and exactly one object of the documented type has that name in the same PLC (`SoftwareUnit` → unit, `NonUnitDB` → PLC-root DB). |

Every relationship response now carries `providerScope`: the providers and edge
kinds declared for the selected node kind, or `unsupported` with a reason for
project, folder, device group, device, namespace and unsupported-software nodes.
Unknown kinds are explicitly unsupported.

New native collections in the capture:

- project subnets (`/projects/<p>/subnets/<s>`) and their IO systems
  (`…/ioSystems/<name>`), under a virtual `Subnets` folder; same paths as the
  resolved references in `hardware_inspect`, which now also returns the IO-system `path`;
- PLC watch and force tables with their groups (tables keep the watch tools'
  `/watch/<group>/<table>` paths; groups use `/watchGroups/…`, force tables `/forceTables/…`);
- PLC external sources and groups (`/sources/…`, names only);
- explicit `unsupported` coverage rows for PLC alarm text lists and project/global libraries.

Selected subnet and IO-system nodes scan every bounded project network interface
(`maxNetworkVisited`, default 20,000, ceiling 50,000) because the associations
are observed from the hardware side. Configured associations never establish
cables, ports or physical topology.

## Offline evidence

Run at the branch's source commit with a clean tree:

- `dotnet run --project tests/TiaMcp.ProjectModel.Tests -c Release`: 15/15,
  including the two new checks. The authored oracle covers escaped paths,
  duplicate observations, an unresolved internal subnet, a foreign controller
  owner, nested and unresolved technology references, unit/DB name ambiguity,
  partial hierarchy and store acceptance/traversal of all provider edges.
- `python -B tests/ProjectModelContracts.py`: 10/10; the new edge kinds validate and an unknown kind is rejected.
- `python -B -m unittest test_project_explorer_relationships` (from `scripts/`): 5/5 qualifier-oracle tests.
- `node --test` explorer model/client/relationship-scope tests: 30 passed, 2 skipped as before.
- The Openness worker builds with zero warnings and errors.

## Native qualification step (not run)

Run only when the TIA machine is free. Package the committed branch, then point
the qualifier at that immutable bundle:

```powershell
pwsh scripts/Package.ps1 | Select-Object -Last 1 | Set-Content artifacts/project-explorer-release.txt
python scripts/qualify_project_explorer_relationships.py --report artifacts/project-explorer-native-relationships.json
python scripts/qualify_project_explorer_relationships.py --verify-report artifacts/project-explorer-native-relationships.json --report artifacts/project-explorer-native-relationships-final.json
```

The first run creates a fresh owned scratch (CPU 1511-1PN plus the contracted
IM 155-6AU02 V6.4 head), adds one subnet and IO system through prepared
`hardware_add`, `network_configure` and `network_io_assign`, and requires all
three network edge kinds. Its oracle derives explorer paths from native names
in independent `hardware_inspect` reads, then checks outgoing hardware and
incoming subnet/IO-system graphs, the new collection nodes and the portable
export. The second run reopens that saved fixture read-only.

Technology and unit providers need fixtures that contain those objects. Audit an
existing saved project read-only and require positive evidence explicitly:

```powershell
python scripts/qualify_project_explorer_relationships.py --project <saved.ap21> --require technologyConnections softwareUnitRelations --report artifacts/project-explorer-native-relationships-units.json
```

## Still unqualified

- All three providers and the new collections on a native V21 project.
- Positive technology-connection and unit-relation fixtures.
- Fresh desktop/native transport acceptance ([checklist](../guides/project-explorer.md#desktop-mcp-apps-host-acceptance-manual-open)).
- Alarm text lists and libraries remain outside the tree; physical topology stays unsupported.
