# Native re-runs and new qualifications — 29 October 2026

On 28 October, 40 new records re-ran 44 of the 74 records whose raw reports were lost
([re-runs](evidence-reruns-20261028.md)). On 29 October the rest that can run on this PC were
re-run, and four open workstreams gained native evidence: web user pages (D12), OPC UA
engineering (D04), the Add-In companion (X04) and the online login route (D09). Each re-run is
named `<original stem>.rerun.20261029`, keeps its raw reports in the main checkout's evidence
store, and replaces the lost record in the features that cited it. The 21 records no feature
cites any more are retired verbatim to
[`catalog-evidence-retired-20261029-reruns.json`](../history/catalog-evidence-retired-20261029-reruns.json)
(catalog `0.4.70`).

## How the runs were made

- **Frozen builds in the project.** Each native run used a Debug build of one commit,
  frozen with `git archive` into a short folder under the main checkout (`artifacts/f`,
  `artifacts/fh`, `artifacts/or4`, …), and was curated from a worktree at that commit, so
  each record names the commit its binaries came from. TIA refuses scratch paths past about
  143 characters, so the folders are short. The typed resolution runners need native paths of at
  most 57 characters and the seeders accept runs only below `<exe>/../../../../../../artifacts`;
  they ran from a copy at `D:\Entwicklung\TIA_MCP-public\x`.
- **Openness access.** The library seeders hung on 28 October because a native tool outside
  the Openness AllowList waits at TIA's access prompt. Debug builds of native tools now
  register themselves (`scripts/native/Directory.Build.targets`, `490365c`), and the library
  harness writes seeder output to a file, so its timeout fires even while another process holds
  the output open.
- **Two lanes.** Typed engineering and typed runtime ran side by side in separate TIA instances;
  PLCSIM runs went one at a time, because an owned PLCSIM route requires its instance to be the
  only registered one.

## Re-runs

| New record | Replaces | Commit | Reports | Recorded steps | Features citing it |
| --- | --- | --- | --- | --- | --- |
| [`local.engineering.boundaries.rerun.20261029`](../../evidence/local.engineering.boundaries.rerun.20261029.json) | `local.engineering.boundaries.20261024` | `9774d1a` | 1 | — | 1 |
| [`local.engineering.interruption.20261029`](../../evidence/local.engineering.interruption.20261029.json) | `local.engineering.interruption.rerun.20261024` | `9774d1a` | 1 | — | 1 |
| [`local.explorer.relationship-scope.rerun.20261029`](../../evidence/local.explorer.relationship-scope.rerun.20261029.json) | `local.explorer.relationship-scope.20261024` | `9774d1a` | 1 | 80 | 2 |
| [`local.hmi.bridge.rerun.20261029`](../../evidence/local.hmi.bridge.rerun.20261029.json) | `local.hmi.bridge.20261025` | `697aabe` | 4 | 384 | 4 |
| [`local.library_protected.rerun.20261029`](../../evidence/local.library_protected.rerun.20261029.json) | `local.library_protected.20261027` | `490365c` | 1 | 60 | 15 |
| [`local.library_scopes.rerun.20261029`](../../evidence/local.library_scopes.rerun.20261029.json) | `local.library_scopes.20261025` | `490365c` | 1 | 63 | 3 |
| [`local.library_unit_relations.rerun.20261029`](../../evidence/local.library_unit_relations.rerun.20261029.json) | `local.library_unit_relations.20261027` | `490365c` | 1 | 58 | 14 |
| [`local.library_units.rerun.20261029`](../../evidence/local.library_units.rerun.20261029.json) | `local.library_units.20261025` | `490365c` | 1 | 49 | 2 |
| [`local.typed.crossscope.engineering.rerun.20261029`](../../evidence/local.typed.crossscope.engineering.rerun.20261029.json) | `local.typed.crossscope.engineering.20261028` | `9774d1a` | 1 | 4 | 1 |
| [`local.typed.insertion.rerun.20261029`](../../evidence/local.typed.insertion.rerun.20261029.json) | `local.typed.insertion.20261025` | `9774d1a` | 1 | 2 | 1 |
| [`local.typed.native-boundaries.rerun.20261029`](../../evidence/local.typed.native-boundaries.rerun.20261029.json) | `local.typed.native-boundaries.20261025` | `9774d1a` | 1 | 4 | 1 |
| [`local.typed.resolution.rerun.20261029`](../../evidence/local.typed.resolution.rerun.20261029.json) | `local.typed.resolution.20261025` | `9774d1a` | 1 | 20 | 1 |
| [`local.typed.route-profiles.rerun.20261029`](../../evidence/local.typed.route-profiles.rerun.20261029.json) | `local.typed.route-profiles.20261025` | `9774d1a` | 1 | 40 | 1 |
| [`local.typed.route-transitive-capture.rerun.20261029`](../../evidence/local.typed.route-transitive-capture.rerun.20261029.json) | `local.typed.route-transitive-capture.20261027` | `9774d1a` | 1 | — | 1 |
| [`local.typed.route-transitive-effects.rerun.20261029`](../../evidence/local.typed.route-transitive-effects.rerun.20261029.json) | `local.typed.route-transitive-effects.20261027` | `9774d1a` | 1 | 28 | 1 |
| [`local.typed.routes.engineering.rerun.20261029`](../../evidence/local.typed.routes.engineering.rerun.20261029.json) | `local.typed.routes.engineering.20261025` | `9774d1a` | 1 | 24 | 1 |
| [`local.typed.runtime.rerun.20261029`](../../evidence/local.typed.runtime.rerun.20261029.json) | `local.typed.runtime.20261024` | `9774d1a` | 1 | 34 | 1 |
| [`local.typed.runtime.reruns.rerun.20261029`](../../evidence/local.typed.runtime.reruns.rerun.20261029.json) | `local.typed.runtime.reruns.20261024` | `9774d1a` | 1 | 4 | 1 |
| [`local.typed.wave9.engineering.rerun.20261029`](../../evidence/local.typed.wave9.engineering.rerun.20261029.json) | `local.typed.wave9.engineering.20261028` | `9774d1a` | 1 | 40 | 1 |
| [`local.wave-kinds.probe.rerun.20261029`](../../evidence/local.wave-kinds.probe.rerun.20261029.json) | `local.wave-kinds.probe.20261027` | `9774d1a` | 1 | 14 | 1 |

"—" marks reports whose runner records no per-step list (interruption and capture runners).

Not reproduced:

- **Typed resolution, stale phase.** `stale-db-ambiguity` adds a second `Tgp_ResolutionOne` in
  `TiaMcp_UnitB` and expects `AmbiguousOperand`. Since `a808569` (26 October) the resolver
  compares qualified names, and that DB is `TiaMcp_UnitB.Tgp_ResolutionOne`, so prepare resolves
  the root DB, which is correct. Both stale runs stop at that case; the record covers the standard
  and namespace phases.
- **`local.explorer.native-providers.20261027`** could not be re-run here; it is dropped from
  `project_model_relationships`, which `local.explorer.relationships.20261028` covers.
- **Not re-runnable on this PC:** `local.library_upgrades.20261024`, `local.library_vendor.20261025`
  and `local.library_dmc_prerequisites.20261025` need vendor and DMC inputs that are gone.

## New qualifications

| Record | Commit | Result |
| --- | --- | --- |
| [`local.access.qualify.20261029`](../../evidence/local.access.qualify.20261029.json) | `9774d1a` | `webserver_user_pages_generate` (D12): the CPU's application name, HTML directory and default page now persist after a reopen. Generating the web DBs does not write them; the worker sets `WebserverApplicationName`, `WebserverHTMLDirectory` and `WebserverDefaultHTMLPage` afterwards (`5224ed0`). |
| [`local.opcua_changes.guarded.20261029`](../../evidence/local.opcua_changes.guarded.20261029.json) | `6f0678f` | OPC UA server-interface import, enable and delete, `opcua_role_mapping_set` with standard roles, and `opcua_engineering_export` (D04), on the build with the restore guard; see the findings. It replaces the same day's [`local.opcua_changes.20261029`](../../evidence/local.opcua_changes.20261029.json) (`1b743ba`, interface kinds) and [`local.opcua_changes.roles.20261029`](../../evidence/local.opcua_changes.roles.20261029.json) (`60efc26`, role mappings), which ran without it. |
| [`local.addin.companion.20261029`](../../evidence/local.addin.companion.20261029.json) | `66d9fc9` | The Add-In companion (X04): installed in TIA, it sent a project-tree selection from inside TIA's Add-In sandbox, and the Host bound it to the saved review with the enrolled hashes. |

## Findings

- **OPC UA reference namespaces crash TIA.** `ReferenceNamespaces.Create` terminates TIA V21
  Update 3 ("Unexpected exception", Portal disposed) for every NodeSet tried, including the OPC
  Foundation DI 1.05.0 NodeSet, headless and with the user interface, server on or off. The two
  reference-namespace kinds are `unavailable_target` and count as blocked.
- **OPC UA role mappings.** V21 creates a custom role only in a SIMATIC server interface's
  namespace and fails silently elsewhere; standard roles are added with `AddStandardOpcUaRole`.
  `SetPermission` does persist, but each write saves the role and leaves earlier role and
  permission objects stale, and a new standard role first lists each standard namespace twice.
  Details in the [OPC UA workflow](../reference/opc-ua-workflow.md).
- **OPC UA restores leave the Portal stale.** TIA caches role mappings and namespace access rows
  per Portal process, keyed by an object id the recovered copy keeps. After restoring an
  interface import, the restoring Portal still showed the removed interface's namespace in the
  required role mappings, and its next OPC UA change saved those rows into the recovered copy;
  after restoring a delete, the returned interface's rows were missing and stayed missing. A fresh
  Portal opening the copy shows exactly the checkpoint. The worker now refuses OPC UA changes in a
  Portal that restored any OPC UA change (`StaleRevision`, `ccca071` and `6f0678f`), and the runner
  compares every restore in a fresh Portal.
- **OPC UA exports.** TIA's server export has an empty first namespace URI; the reviewer reads it
  as `urn:tiamcp:unnamed-local-namespace` (`07adef1`). Importing one interface moves every
  interface's `lastModified`, so interfaces are compared by export content.
- **TIA's Add-In sandbox.** TIA runs Add-Ins in partial trust. There `NamedPipeClientStream`,
  `SafeHandle`, `Marshal.GetLastWin32Error` and `JavaScriptSerializer` demand full trust
  ("Request failed."). The companion now opens the Host pipe through kernel32 handles and uses its
  own bounded JSON (`9b468c1`); `scripts/Test-AddInSandbox.ps1` replays both under the declared
  permissions in the offline suite.
- **Typed runtime and `~PEData`.** In 7 of 34 typed runtime cases TIA added `System/~PEData.1`
  next to its `.info` file when the project opened, before any runtime step. Maintainer decision:
  the semantic preservation rule excuses that open-time file like the `.info` file (`b30c1f9`).
- **Test Suite TS-Q01.** The first native dispatch of a style job showed that the worker returned
  two checkpoint files (archive and baseline) for one planned resource, which the operation store
  refuses. The worker now returns one manifest that pins both by SHA-256 (`d4ee5e8`). The
  operator's scope observation itself was accepted.
- **Online login (D09).** The online compare, station upload and DB-snapshot routes now answer
  TIA's login challenge once with the owned instance's generated scratch runtime user, and only
  when the CPU offers project-user authentication (`f95f448`); TLS stays untrusted.
  `scripts/qualify_online_routes.py` (`768f7c7`) runs them against an owned PLCSIM instance.
- **Curation.** The curator now reads HMI bridge reports (profile gates) and Host shutdown tool
  calls (`697aabe`).
