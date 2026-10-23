# Project explorer contract fixtures

These are deterministic authored examples, not native Siemens qualification evidence.

`representative.project-model.json` contains 28 nodes with two PLCs sharing a display name, four `Start` blocks in distinct exact scopes, a software unit and namespace, real empty native groups, virtual display folders, hardware inventory, HMI content unavailable, protected and unavailable services, Unicode/adversarial display text, cyclic calls, readers/writers and an unresolved target. Configured I/O is illustrative recorded evidence; physical layout is explicitly unsupported.

`expected-hierarchy.json` records the fixture roots/children and collision paths. The C# model tests additionally assert independent explicit expected ancestry and child sets. `expired.project-model.json` exercises portable expired data. Its content remains viewable offline but its selection is not fresh. `unsupported-provider.json` exercises provider absence without confusing it with empty content.

Validate schemas and positive/negative fixtures using `python tests/ProjectModelContracts.py` with `jsonschema>=4.18`. Run the detached model, cursor, provider and synthetic cached-query tests without Siemens using `dotnet run --project tests/TiaMcp.ProjectModel.Tests`. Native capture latency requires the separate native qualification harness; the synthetic model benchmark cannot certify it.

Exact selection uses snapshot, worker generation, project-copy identity, node ID and canonical path. The metadata hash covers exposed metadata only. Preview provider responses have separate content revisions and timestamps; they may never reuse a metadata hash to claim unchanged PLC or HMI content.
