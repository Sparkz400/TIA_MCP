# TIA Portal V19 adapter (read-only)

A Host started with `--tia-version 19` runs `TiaMcp.Openness.V19`, a separate .NET Framework 4.8 worker built
against the installed V19 Openness SDK. It reads V19 projects; it never changes, compiles, saves or archives them.
To edit a V19 project, upgrade a copy into V21 with `project_upgrade` on a normal (V21) Host.

This is the first adapter of `tia.multiversion` (X12): one worker, build and evidence per TIA major version.
V15.1, V17, V18 and V20 have no adapter.

## Prerequisites

- TIA Portal V19 with its Openness SDK (`PublicAPI\V19\Siemens.Engineering.dll`; the worker reads the location
  Siemens registers under `HKLM\SOFTWARE\Siemens\Automation\Openness\19.0\PublicAPI\19.0.0.0`) and the licenses
  V19 needs to open the project.
- Membership in the `Siemens TIA Openness` group, as for V21.
- The worker in the Openness AllowList. Debug builds register themselves once
  `scripts/Register-DevOpennessWorker.ps1 -Grant` has run elevated; otherwise TIA asks for access.

## Build and start

The project is not part of `TiaMcp.slnx`, so the solution keeps building where V19 is not installed. Release
bundles do not contain it yet.

```powershell
dotnet build src/TiaMcp.Openness.V19/TiaMcp.Openness.V19.csproj
```

Start the Host with `--tia-version 19`; `--worker` overrides the default
`src/TiaMcp.Openness.V19/bin/Debug/net48/TiaMcp.Openness.V19.exe`. An MCP client configuration:

```json
"tia-v19": { "command": "<checkout>\\src\\TiaMcp.Host\\bin\\Debug\\net10.0-windows\\TiaMcp.Host.exe",
             "args": ["--tia-version", "19", "--timeout-seconds", "300"] }
```

`--tia-version 19` is refused together with the qualification modes, `--addin-handoff` and the library apply
options. `--toolsets` still applies on top of the V19 surface.

## Tools

The Host advertises the base tools (`feature_catalog`, `host_build_identity`, `artifact_*`) and these twelve, with
V19 descriptions; every other tool is neither listed nor callable, and no prompts are offered.

| Tool | V19 behavior |
| --- | --- |
| `tia_discover`, `tia_attach`, `tia_session_status`, `tia_detach` | Running V19 Portals only (Openness V19 sees no other version). Attaching never changes, saves or closes the user's projects. |
| `tia_start` | A worker-owned V19 instance without UI; detaching disposes it without saving. |
| `tia_capabilities` | `tiaVersion: 19`, the API assembly version, `readOnly: true` and the implemented `operations`. |
| `project_open` | An existing `.ap19` file in an empty worker-owned instance. Any other extension is `ProjectVersionUnsupported`; there is no upgrade. `readOnly: true` opens a secondary project, on which TIA also refuses exports. |
| `project_inventory` | Devices with address ranges, PLCs and blocks, including software and safety units and generated system groups. |
| `software_inspect` | The V21 shape (`softwareScopes`, `blocks`, `tagTables` with tags and constants, `types`) without technology objects, group paths and unit relations. |
| `block_read`, `type_read`, `tag_table_read` | Unchanged native SimaticML export with SHA-256 (V19 schema). The `format` options (`simaticSd`, `generatedSource`) are refused with `UnsupportedOnTiaVersion`. |

A worker operation the adapter does not implement is refused with `UnsupportedOnTiaVersion` before any native call.

## Paths are version-independent

The adapter builds object paths with the V21 worker's code (`ObjectIdentity`), so the path of an object in a V19
project and in its V21 upgrade differ only in the `/projects/<name>` segment (`project_upgrade` names the copy
`<name>_V21`). Native XML is not interchangeable between versions: compare content, not bytes.

## Fixtures and qualification

`scripts/native/V19ProjectSeeder` creates an owned V19 fixture under the repository's `artifacts` folder (one
CPU 1511-1 PN; a tag table with tags and a constant; a PLC data type; FC, FB, global DB, instance DB and OB; a
software unit with its own type, FB, DB and tag table, all generated from SCL sources), compiles, saves and
archives it. `hold` keeps a project open in its own V19 Portal for attach and locked-source checks.

`scripts/qualify_tia_v19.py --run-native` seeds a fixture, checks the V19 surface, reads every seeded object through
the adapter, then upgrades the project folder and its archive with a V21 Host and compares the upgraded objects with
their V19 exports. `scripts/test_tia_v19_surface.py` keeps the Host's tool map, the worker's operations and the
qualifier in step offline.
