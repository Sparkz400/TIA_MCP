# Target capability discovery

`target_capabilities` reads the selected V21 engineering object's native self-description and a fixed set of typed services. It does not export a block, prepare a change, compile, log into safety, start a simulator, or read/write a running PLC. It writes no artifacts. Call it again after changing the project or target; results are point-in-time observations.

The tool takes a `selectors` object using the shared object model:

```json
{"kind":"plc","projectName":"Example","plcPath":"<exact inventory PLC path>","maxItems":500}
```

```json
{"kind":"block","objectPath":"<exact inventory block path>","projectName":"Example","maxItems":500}
```

Other kinds are `project`, `hardware`, `type`, `tagTable`, `technology`, `softwareUnit`, and `safetyUnit`. `name` may replace `objectPath`; all supplied selectors must agree and an ambiguous name is rejected with candidate identities. `scopePath` distinguishes software units. Existing `blockPath`/`blockName`, `typePath`/`typeName`, `tagTablePath`/`tagTableName`, `hardwarePath`/`hardwareName`, and `technologyPath`/`technologyName` are accepted aliases. With no object selectors, the default is the project, or the PLC when `plcPath` is supplied.

## Response contract

The response includes `schemaVersion: 1`, `origin`, `observedAtUtc`, project metadata, the common identity (`projectPath`, `plcPath`, `scopePath`, `objectPath`, `kind`, `name`), target features, metadata, service probes, protection observations, warnings and completeness.

Each feature is separate evidence about a public tool or change kind:

```json
{
  "featureId": "change.graphical_operand_set",
  "api": {"status":"available","reasons":["Native typed API is exposed."]},
  "implementation": {"status":"partial","limitations":["Existing symbolic operands only."]},
  "qualification": {"status":"unverified","evidenceIds":[]},
  "availability": {"status":"conditional","reasons":["This exact target has not been qualified."]}
}
```

This example is illustrative. A real response describes its actual observation and any target blockers. Feature IDs match tool names; mutations use `change.<kind>`. Unlisted features were not evaluated and must not be treated as unsupported.

| Dimension | Meaning |
|---|---|
| API `available` | A native typed interface or service is exposed; this does not establish payload validity or authorization. |
| API `unavailable` | A targeted typed service probe returned no service on the selected object or its documented owning hardware. |
| API `unknown` | A probe failed, hit an ownership bound, or additional per-block probes are needed. |
| Implementation | `implemented`, `partial`, or `not_implemented`, independent of what Siemens exposes. |
| Qualification | `verified`, `unverified`, or `failed`, with evidence IDs. This discovery endpoint emits `unverified` for operation qualification on arbitrary targets. |
| Availability | `unsupported` for an inapplicable target, absent native API, or absent implementation; `blocked` for a known protection/scope constraint; `unknown` for unresolved API availability; `conditional` when preconditions, partial coverage or target qualification remain; `available` only when all required evidence supports it. |

Fixture results do not make an unrelated CPU or project verified. The pure `CapabilityRules` helper enforces this distinction. A later qualification record must bind to its exact fixture manifest, target and artifacts before contributing evidence.

In the bundled `feature_catalog`, `verified` also requires every artifact of each cited evidence record to be retained in this repository, and `available` requires `verified`. Evidence whose artifacts exist only in the ignored `artifacts/` folder stays cited as history under `unverified`. `scripts/test_contracts.py` enforces both rules and also requires, for `verified`, a passed curated summary under `evidence/` for every cited record in which every bound tool answered without error; see the [catalog reconciliation](../verification/catalog-reconciliation-20261024.md) and [requalification](../verification/catalog-requalification-20261024.md).

## Observed metadata and limits

- Native `GetAttributeInfos` supplies access flags and potentially supported CLR types. CLR enum names are reported only when an exposed supported type actually is an enum. Enum values do not imply every value is valid for the selected CPU or configuration.
- Attribute values are not read. Sensitive fields are marked excluded. Generic native attribute descriptors do not expose ranges or units; those fields report `not_exposed` instead of guessed values.
- `GetServiceInfos`, `GetInvocationInfos`, and `GetCompositionInfos` report descriptors only. Discovered actions are never invoked; compositions are not recursively expanded.
- TO parameter names and their exposed attribute metadata are included within the same budget. No parameter value is read. CPU-supported TO creation type/version enumeration remains an explicit gap.
- Typed compiler, cross-reference, software-unit, network, simulation-setting, download, and safety probes are selected by target kind. Download and safety are probed on owning hardware, not assumed to be services on `PlcSoftware`.
- A missing PLC-wide cross-reference service does not imply the analysis tools are unsupported: their per-block fallback remains `unknown` until those targets are inspected or analysis is run.
- Known block/type protection, inconsistent export targets, generated system groups, safety login state and applicable safety boundaries are reported. All preconditions are rechecked by the actual mutation workflow. No new authorization is inferred.
- `maxItems` is 1–2000 and bounds metadata descriptors and enum names together. Nested type lists cap at 32 and enum lists at 128 names. Service probes and feature rows are a fixed target-specific set. Shared object lookup has its own 50,000-item uniqueness bound; an incomplete lookup fails rather than choosing an arbitrary candidate.
- `scopeComplete` means the bounded observation finished without warnings or unresolved service probes. `complete` remains false: metadata inspection does not establish every API feature, import payload, protection state, live behavior, library option or physical configuration.

Siemens documents this self-description API in [V21 self-description support](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/self-description-support-for-attributes-navigators-actions-and-services) and [object structure and attributes](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-projects-and-project-data/determining-the-object-structure-and-attributes). Installed V21 XML API references were also checked for the exact descriptor properties used here.

## Recorded qualification

`scripts/verify_target_capabilities.py` drives the actual stdio MCP host. It verifies an existing task-created scratch checkpoint's SHA-256, retrieves it into a unique directory in a new worker-owned TIA session, and detaches that session after the checks. The original checkpoint remains unchanged. The test makes no project mutation or compile calls.

Two successful native runs are recorded:

- `artifacts/target-capabilities-live.json`: CPU 1511-1 PN V4.0; project, PLC, SCL/STL/LAD/FBD blocks, eight hardware targets, a PLC data type and a tag table. Native network-service availability differed correctly between hardware targets. `maxItems: 1` returned exactly one metadata item and reported truncation. The non-F CPU exposed no safety service and reported that workflow unsupported.
- `artifacts/target-capabilities-safety-live.json`: CPU 1511F-1 PN V4.1; safety administration was exposed, while the selected generated/protected F-block reported blocked export and direct mutation. The generated safety import route was also blocked.

In both runs every feature's arbitrary-target qualification remained `unverified`; observed native services did not promote that status. Before/after project inventories, software metadata and hardware metadata matched. A sample standard block's native XML matched after removing only top-level export `DocumentInfo`, and the source archive SHA-256 was unchanged. This is observable metadata and sample-content preservation, not a whole-project binary equivalence proof.

The recorded runs do not cover a TO, safety-unit creation, a protected non-generated user F-block, or every language/CPU combination. Follow-up pure tests passed for the safety-import eligibility predicate: only consistent unprotected F_LAD/F_FBD/F_DB blocks pass; F_CALL and other unsupported languages remain excluded. Generated-object classification uses the common canonical path helper so a user folder merely named `systemGroups` is not mistaken for a native system group. These predicate refinements passed the coordinated build/test run after the recorded native sessions; they do not broaden the historical live evidence.
