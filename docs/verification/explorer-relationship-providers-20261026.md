# Explorer relationship providers and collection scope — 2026-10-26

All three providers and the new collections have passed native V21 fixtures.
Configured-network and collections passed on 26 October ([Native result](#native-result)).
Technology-connection and software-unit-relation passed on 27 October
([Technology and unit providers](#technology-and-unit-providers--27-october-2026)).
Nothing here is desktop-host or release qualification.

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

Run at source commit `75f0058` with a clean tree:

- `dotnet run --project tests/TiaMcp.ProjectModel.Tests -c Release`: 15/15,
  including the two new checks. The authored oracle covers escaped paths,
  duplicate observations, an unresolved internal subnet, a foreign controller
  owner, nested and unresolved technology references, unit/DB name ambiguity,
  partial hierarchy and store acceptance/traversal of all provider edges.
- `python -B tests/ProjectModelContracts.py`: 10/10; the new edge kinds validate and an unknown kind is rejected.
- `python -B -m unittest test_project_explorer_relationships` (from `scripts/`): 5/5 qualifier-oracle tests.
- `node --test` explorer model/client/relationship-scope tests: 30 passed, 2 skipped as before.
- The Openness worker, Host and `TiaMcp.Tests` build with zero errors.
- `scripts/Test-Offline.ps1` at `56ad925` passed every
  suite from a short detached checkout (`D:\tx`): the Python phase ran 2,023 tests
  (1 skipped), the managed and host checks reported zero failures, and the script
  ended `PASS Offline validation (All)`. Log SHA-256
  `6c7bd3289f6fab78910b28b1b00544454bd2a7a72bcc2ebdbfbade448af395a3`.
  From this worktree's long path, two `TiaMcp.Tests` checkpoint checks hit TIA's
  143-character archive staging limit; that is the known long-checkout boundary,
  not a code failure. An earlier run was stopped on purpose because its
  startup-lifecycle checks start mock `Siemens.Automation.Portal.exe` processes
  while a live native corpus was running.

## Native result

Evidence: [`evidence/local.explorer.native-providers.20261026.json`](../../evidence/local.explorer.native-providers.20261026.json)
(source `7f1492d`, clean tree; release `tia-mcp-v21_20261026_124042_240b4494`).

- The fixture run created a fresh owned scratch (CPU 1511-1PN plus an IM 155-6AU02
  V6.4 head) and made 8 prepared changes: one subnet, one IO system and one IO
  device. All 56 steps passed, and the owned session detached with host exit 0.
- The explorer's `connectedSubnet`, `ioSystemController` and `ioSystemDevice` edges
  exactly matched the 4 associations derived from independent `hardware_inspect`
  reads. This held for outgoing queries from both interfaces, for incoming queries
  from the subnet and IO system, and in the portable export.
- The 95-node capture had a complete `nativeHierarchy` and contained the new
  `subnet`, `ioSystem`, `watchTableGroup`, `forceTable`, `externalSourceGroup` and
  `externalSource` nodes.
- The `--verify-report` rerun reopened the saved fixture with zero prepared changes
  (30 steps, all passed) and reproduced every edge.
- Native capture took 337 and 499 ms; relationship queries took 325–476 ms. One
  machine and one small fixture; this is not a latency guarantee.

That fixture had no technology objects and no unit relations; see the next section.

## Technology and unit providers — 27 October 2026

Evidence: [`evidence/local.explorer.native-providers.20261027.json`](../../evidence/local.explorer.native-providers.20261027.json)
(source `97ba318`, clean tree; release `tia-mcp-v21_20261027_064910_1e3d1e2a`, built
from `09b0e30` with identical host and worker sources).

The fixture mode now also authors both providers' objects. Neither unit relations nor
TO hardware connections have a prepared change kind, so after the network fixture:

- a prepared `hardware_plug` adds a TM PosInput 2 V2.0 module
  (`ExplorerEncoderHardware`, the module of `fixtures/encoder-hardware`) to the CPU rack;
- the registered fixture tool `scripts/native/UnitSeeder` (`explorer-relations` mode)
  creates the software units `ExplorerUnitA` and `ExplorerUnitB` with the `SoftwareUnit`
  relation A → B. It sets the module channel to `MotionControl`, creates a
  `TO_ExternalEncoder` V10.0 (`ExplorerEncoder`), connects its sensor interface to the
  channel and compiles the PLC (0 errors, 1 warning about the technology alarm text list).

Result (fixture run 67 steps, read-only rerun 36 steps with zero prepared changes; all
passed, host exit 0 both times):

- `technologyConnection` edges from `ExplorerEncoder` resolve only to the TM PosInput
  module and match the hardware paths in `technology_read`. Four native fields
  (channel parent, input/output module, …) yield edges to the same module.
- The `unitRelation` edge from `ExplorerUnitA` resolves to unit B; unit B has none. Both
  match the `software_inspect` relations.
- Both graphs report `complete` `providerCoverage` and declare the provider in
  `providerScope`. The configured-network edges again match their 4 oracle associations.
- Native capture took 398 and 1,730 ms (147 nodes); relationship queries took 470–2,097 ms.
  One machine; this is not a latency guarantee.

Five failed attempts preceded the passing sixth run; their reports stay under the run's
`artifacts/`. Attempt 1 hit the Openness firewall because the new release worker was not
yet registered. The other four exposed qualifier and seeder defects, all fixed in this branch:

- `hardware_plug` requires `compile=true`;
- the module and its submodule share a name;
- coverage is reported as `providerCoverage`;
- `software_inspect` lists units under `softwareScopes`, so the unit check had never run.

## Native qualification step

Run only when the TIA machine is free. Package the committed branch, then point
the qualifier at that immutable bundle. A separate release file leaves the
default `artifacts/project-explorer-release.txt` of the other explorer qualifiers unchanged:

```powershell
pwsh scripts/Package.ps1 | Select-Object -Last 1 | Set-Content artifacts/explorer-relationships-release.txt
python scripts/qualify_project_explorer_relationships.py --release-file artifacts/explorer-relationships-release.txt --report artifacts/project-explorer-native-relationships.json
python scripts/qualify_project_explorer_relationships.py --release-file artifacts/explorer-relationships-release.txt --verify-report artifacts/project-explorer-native-relationships.json --report artifacts/project-explorer-native-relationships-final.json
```

The first run creates a fresh owned scratch (CPU 1511-1PN plus the contracted
IM 155-6AU02 V6.4 head), adds one subnet and IO system through prepared
`hardware_add`, `network_configure` and `network_io_assign`, and requires all
three network edge kinds. It then plugs the TM PosInput module and runs the seeder
as described above. Its oracle derives explorer paths from native names
in independent `hardware_inspect` reads, then checks outgoing hardware and
incoming subnet/IO-system graphs, the new collection nodes and the portable
export. The second run reopens that saved fixture read-only.

Build the seeder first and register it in the Openness AllowList (no elevation after
the one-time `Register-DevOpennessWorker.ps1 -Grant -AllNativeTools`). Register a new
release worker path the same way, otherwise `tia_start` waits on TIA's firewall prompt:

```powershell
dotnet build scripts/native/UnitSeeder -p:TiaApiDirectory="<Portal V21>\PublicAPI\V21\net48"
pwsh scripts/Register-DevOpennessWorker.ps1 -WorkerPath scripts/native/UnitSeeder/bin/Debug/net48/UnitSeeder.exe
```

The seeder resolves fixture paths under its own checkout's `artifacts/`, so run the
qualifier from that checkout with the report under `artifacts/`. Fixture and
`--verify-report` runs always require positive technology and unit evidence. To audit
another saved project read-only, require it explicitly:

```powershell
python scripts/qualify_project_explorer_relationships.py --release-file artifacts/explorer-relationships-release.txt --project <saved.ap21> --require technologyConnections softwareUnitRelations --report artifacts/project-explorer-native-relationships-units.json
```

## Still unqualified

- Other technology connections (axis, measuring input, output cam; tag, block and address
  targets) and unit relations to `NonUnitDB`/`TODB`, or unresolved and ambiguous names.
  These have offline oracle tests only.
- Broader network topologies: several subnets, shared devices, redundancy and couplers.
- Populated watch tables and external sources against an independent oracle.
- Fresh desktop/native transport acceptance ([checklist](../guides/project-explorer.md#desktop-mcp-apps-host-acceptance-manual-open)).
- Alarm text lists and libraries remain outside the tree; physical topology stays unsupported.
