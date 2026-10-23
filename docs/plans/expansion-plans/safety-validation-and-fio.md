# Safety validation and F-I/O implementation plan

Updated 17 October 2026. D14 remains a full release obligation. This workstation
has no Siemens product or SDK. Portable delivery, documented native routes and
native qualification remain separate. No physical operation or functional-safety
approval is authorized by this plan.

## Current implementation and evidence

The [fresh baseline increment](../../verification/plc-baseline-20261017.md) adds explicit checkpointed PLC compilation before baseline capture and after recovery, with exact original-call evidence. The frozen 41-suite aggregate and all 24 package stages pass. Native SDK/compiler/signature behavior, copied endpoint correspondence and the full family plan remain open.

- EngineeringTools exposes safety_inspect and shared prepared changes.
  OpennessSession.Inspection.cs resolves the owning PLC DeviceItem and reads
  runtime groups, settings, program/block signatures and completeness. It needs
  TIA. Changes.cs implements guarded safety_import_xml, not general F-I/O or
  Safety Validation Assistant management. The worker references Safety.dll but
  has no SafetyValidation assembly reference.
- Existing safety runners/auditor share exact lifecycle, scratch admission,
  recovery-source and compiler-evidence helpers. Fresh baseline compilation and
  independent compiler coverage across remaining consumers are still open.
- The retained api-coverage-inventory-20261014.json records 283 Safety members/18
  types and 316 SafetyValidation members/19 types. This historical metadata is
  not an installed SDK or a current build result. Historical native narratives
  do not replace missing original receipts or qualify later source changes.
- Reuse ArtifactUploadStore, ProjectReviewStore, PackageStorage, bounded report
  patterns, EngineeringIdentityGuard and existing journals/reservations. Preserve
  all failed outcomes and the 106 unavailable historical evidence bindings.
- Device compiler scope is integrated in `43ed561`; independent remaining
  consumers and fresh baseline compilation remain open. The supplied safety
  workflow below passes the frozen 41-suite aggregate and all 23 portable
  package stages, including 96 managed and 87 actual MCP safety checks. It does not close D14 or native acceptance.

## Verified documentation decisions

Official V21 pages were checked on 17 October. Context7 resolved only V20 material;
its synthesized HTTP examples were not used as API contracts.

| Capability | Documented route | Remaining implementation and qualification |
| --- | --- | --- |
| Signatures/configuration | SafetyAdministration on PLC DeviceItem, SafetySignatureProvider on blocks. | Existing native inspection; supplied comparison, exact profile coverage and current native qualification remain. [S1] |
| Safety printout | SafetyPrintout.Print with an enabled PDF/XPS printer, layout and All/Compact options. | Native capture and genuine fixtures. Preserve opaque originals; PDF extraction is not a signature oracle. [S2] |
| F-BaseID | SafetyBaseIdProvider.GenerateBaseId on supporting S7-1200 G2/1500 F-CPUs from V4.1; distributes to connected type-3-address F-I/O. | Broad affected-device mutation; exact firmware/mode/role checks, readback and recovery. Manual/delayed assignment can refuse. [S3] |
| F-capability/F-I/O | V21 documents activation/deactivation and F-I/O parameter writes. | Separate typed capabilities and target/product/protection profiles; generic writes do not establish safety eligibility. [S4] |
| Bulk parameters | SetAttributes with dependency ordering and error callback. | Ignore can continue after a failed field. Require complete individual outcomes and readback; one call is not proof of atomicity. [S5] |
| SVA discovery | Project.GetService<SafetyValidationAssistant>(), ActivationTests, ActivationTestGroups, DeviceQuery. | SDK/reference, actual availability, exact identities and bounded capture. [S6] |
| Activation-test lifecycle | Query, create, duplicate, library copy, modify, import/export. | Exact parent/evaluation-device binding, collision/drift rules, format fixtures and preservation/recovery. [S7] |
| Safety functions | ActivationTest.SafetyFunctions, conditions, validity/result state, lifecycle, result reset. | Reset must retain old evidence and preview effects. Unknown definitions/results remain explicit. [S7] |
| SVA reports | ActivationTestPrintout.Generate writes XLSX. | Native capture and genuine workbook profiles before structured parsing. [S8] |
| SVA trace configuration | SafetyFunction.TraceConfiguration. | Exact test-case signal/instance/timing validation. This does not expose ordinary PLC trace acquisition. [S9] |
| SVA execution | V21 explicitly excludes test execution from Openness support. | No invented Execute API. Separately authorized specialist execution and retained evidence intake; automated alternatives remain research. [S4] |
| Logic/runtime groups/download | Distinct administration and download capabilities. | Separate profiles and specialist/native/bench acceptance; existing import evidence is not blanket approval. [S1] |

## First complete portable workflow

Select an exact PLC in a saved review; upload original before/after safety
inspection JSON and supporting artifacts; inspect a persistent comparison after
upload release and Host restart. Preserve exact bytes, digests and unknown
payloads. Match project/PLC identities, never display names alone. Supplied
observations do not establish authenticity or current target state.

Registered tools: `safety_evidence_review` and `safety_evidence_report`, following
existing review/report conventions. Arguments name the saved review, exact PLC,
two observation uploads and optional supporting source roles. Versioned records
and reports preserve phases, original bytes and expected scope. Report identity,
diagnostic completeness, signature/configuration changes and coverage separately
from authenticity, native qualification and safety approval. Parse UInt64 exactly;
zero/absent signatures cannot imply valid safety logic.

Reuse current 4 MiB input, 16 MiB/source, 64 MiB total source and 128 KiB page
patterns where applicable; bound row counts before fixtures. Reject malformed or
duplicate identities, unsafe paths and changed originals. Partial evidence may
produce an incomplete report, never whole-program validation. Rebuild and compare
the derived report on reopen. Native adapters later feed the same model.

Native changes reuse change_prepare/apply/status and journals. Previews bind
source/session/target, writable metadata, dependencies, checkpoint scope and
independent expected effects. Unknown outcomes never authorize replay.

The [user workflow](../../reference/safety-evidence-review.md) and
[verification record](../../verification/safety-evidence-review-20261017.md) describe
the implemented SA-03 scope. Native PDF/XPS and XLSX files are retained opaquely;
no genuine format parser or vendor report certification is claimed.

## Dependency-ordered work packages

Estimates assume documented APIs/fixtures, exclude product acquisition and bench
scheduling, and do not reduce scope.

| ID | Complete deliverable | Acceptance and dependencies | Estimate |
| --- | --- | --- | --- |
| SA-01 | Reconcile V21 API matrix and exact target/product/role profiles. | Linked symbols/sources and explicit unsupported execution; no fabricated provider. | 1-2 days |
| SA-02 | Finish compiler/source/phase prerequisites in current safety qualification. | Fresh baseline semantics, exact target coverage, negative phase/owner tests and original cleanup receipts. | 2-4 days |
| SA-03 | Supplied-observation workflow delivered: Core comparison, retained sources, Host tools, schemas, fixtures, guide and package integration. | UInt64, missing/duplicate/unknown fields, wrong PLC, partial coverage, tamper/restart/release, bounded pages and actual packaged MCP. Depends on SA-01 and existing stores. | 3-5 days |
| SA-04 | Native exact-owner safety/F-I/O inventory and printout capture. | SDK build, complete original artifacts, protected/unsupported/printer failure tests and independent capture expectations. Depends on SA-03. | 2-4 days plus native window |
| SA-05 | SVA definition/validity/result discovery and XML/XLSX capture. | Genuine grouped/ungrouped/evaluation-device fixtures, explicit omissions and format/version evidence. Depends on SA-01/03. | 3-5 days plus native window |
| SA-06 | Activation-test/function/condition lifecycle and library reuse. | Collision/stale/protected refusal, exact effects/result invalidation, preserved peers, reopen/recovery/interruption. Depends on SA-05 and exact schemas. | 5-8 days |
| SA-07 | SVA trace review/configuration management. | Exact devices, signals, instances, timing and dependent changes; no general acquisition claim. Depends on SA-05/06. | 2-4 days |
| SA-08 | One typed F-I/O parameter profile, then documented full expansion. | Exact per-field outcomes, preserved addresses/peers, compile/signatures, partial bulk failure and independent recovery/readback. Depends on SA-04 and hardware profiles. | 4-7 days plus bench |
| SA-09 | F-BaseID/address dependencies and separately admitted mutations. | Full affected-device manifest, mode refusal, valid addressing, dependent effects and recovery. Depends on SA-08. | 3-5 days plus bench |
| SA-10 | Separate F-capability/runtime-group/logic profiles where supported. | Exact license/protection/ownership, diagnostics/signatures/preservation and specialist acceptance. Depends on SA-04/08. | 5-10 days plus native windows |
| SA-11 | Specialist execution handoff and diagnostic/Test Suite/report composition. | Preserve genuine test identity/state; distinguish definition validity, execution, compilation and approval. Unsupported Openness execution remains explicit. Depends on SA-03/05. | 2-4 days plus specialist work |
| SA-12 | Full version/vendor/scale/recovery matrix and release audit. | Every supported profile has exact source/binary/fixture and independent proof. Missing evidence remains open. Depends on all preceding packages. | 3-6 days plus qualification |

Portable work continues with remaining SA-02 compiler prerequisites alongside
SA-03 supplied-evidence delivery; neither substitutes for the other. Native critical path: SDK, genuine
fixtures, target/license access, then SA-04/05 and independent qualification.
Do not defer useful supplied-evidence delivery while awaiting hardware.

## Native qualification

Retain source/binary hashes, product/API versions, CPU/module identifiers and
firmware, role/license/protection observations, exact project/process/target and
original inputs. Establish expected effects independently. Capture all relevant
diagnostics and required compile targets. Unchanged signatures alone do not
prove unchanged safety behavior.

Qualify clean/failing/unsupported/protected cases, partial bulk effects, concurrent
edits, lost responses, worker/Host interruption, save/reopen and new-location
checkpoint recovery. Keep failed journals. Include duplicate names, renamed
devices, Unicode, opaque attributes, large projects and unrelated-content
preservation. Physical signals, timing, addressing, downloads, motion and actual
activation tests require their own suitable environment and specialist review.

Report availability, API presence, clean compilation and test-definition validity
are different observations. None approves commissioning or closes D14. Every
feasible capability remains a 1.0 obligation.

## Sources

[S1]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/f-related-openness
[S2]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/f-related-openness/safetyprintout
[S3]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/f-related-openness/safetybaseidprovider
[S4]: https://docs.tia.siemens.cloud/r/en-us/v21/what-s-new-in-tia-portal/what-s-new-in-v21/engineering-options
[S5]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-devices/bulk-changing-hardware-parameters
[S6]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-safety-validation-assistant/accessing-safety-validation-assistant
[S7]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-safety-validation-assistant
[S8]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-safety-validation-assistant/generating-activation-test-reports
[S9]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-safety-validation-assistant/getting-the-trace-configuration
