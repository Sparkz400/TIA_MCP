# D05 AutomationML / CAx implementation and remaining gates

Status: first complete offline document workflow implemented; native exchange
and full D05/C02 acceptance remain open. No Siemens product is installed on this
workstation. This plan records the implemented boundary and the next meaningful
work, rather than treating schema-valid XML as a native import plan.

## Research basis, checked 16 October 2026

Context7 library lookup for AutomationML returned no matching library. Primary
AutomationML and Siemens documentation supplied the format/API evidence.

- Siemens V21 [AML file format](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-hardware-data/aml-file-format?contentId=XPbYagciSgxdewCGyWfnuA)
  identifies CAEX 2.15. The portable 3.0 profile is independent document support.
- Siemens [CAx data structure](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-hardware-data/structure-of-the-cax-data-for-importing/exporting?contentId=LWP8K7B_tjF7KNAWyhjd6Q)
  describes writer information, role/type mapping and differing import/export
  attribute behavior. Generic XML inspection cannot establish these semantics.
- Siemens [CAx export](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-hardware-data/export-of-cax-data?contentId=oTsVVnOuYLHlakuW87EhrQ)
  documents `CaxProvider` and project/device export overloads. These are research
  candidates for an installed SDK adapter; this increment does not compile or
  invoke them. The same page explicitly excludes drives, most HMI devices and
  listed Scalance/device types from CAx exchange. C02 drive/TO work therefore
  needs separate supported engineering routes; AML inspection does not remove
  those restrictions. TransferResult messages must be retained as evidence,
  including warnings and nested messages.
- Siemens [logical-address import example](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-hardware-data/import-cax-data-without-logical-address?contentId=mG3Z1_eZRrqdCao0ZIR~hQ)
  supplies the 2.15 element-ID/interface-name link form and nested address fields.
- AutomationML [InternalLink endpoint reference](https://automationml.github.io/AMLEngine2.1/html/P_Aml_Engine_CAEX_InternalLinkType_RefPartnerSideA.htm)
  distinguishes the 3.0 endpoint representation. Independent upstream fixtures
  cover both forms without claiming Siemens provenance.
- Vendored XSDs/fixtures come from `AutomationML/AML-UA-XSLT` at
  `2e6ad1c76e0fb11c306a34f014be3ce7fbea6f97` and
  `AutomationML/automationml-python` at
  `424ebbc93057cd432d70583c1eac29ba10b702a8`.
  [Exact URLs, paths and hashes](../../../third-party/automationml/sources.json)
  and both MIT license notices are retained. No proprietary Siemens fixture is
  synthesized and then described as a native export.

## Delivered offline boundary

Core parses bounded XML with two pinned XSD profiles. Six Host tools provide
committed-upload inspection, immutable before/after comparison, declared impact
review, paged reports, exact downloads and restart discovery/verification.
The existing retained-artifact publication pattern supplies integrity and
incomplete-publication behavior. There is no new native mutation kind or adapter.
The catalog remains unverified for native qualification.

Tests cover public fixtures, synthetic structured address/topology changes,
missing/duplicate IDs and ports, opaque content, XML limits, external resolution
refusal, 10,000-device input, transfer/restart/tampering and actual stdio MCP calls.
Python independently checks public fixture bytes and extracted declarations.
Managed CI and the relocated portable-package workflow include this feature.
See the [user workflow](../../reference/automationml-workflow.md) and
[exact test evidence](../../verification/automationml-workflow-20261016.md).

## Remaining implementation sequence

| Work | Offline continuation | Required external evidence |
| --- | --- | --- |
| Siemens exchange profiles | Add genuine supplied, provenance-pinned whole-project, device and pruned AML fixtures; inspect AR APC/writer metadata and supported role/type vocabularies without inventing native identities. | Independently exported files and exact product/update/catalog versions. |
| Identity and topology | Define explicit source/target project association and review collisions, nested slot/submodule/address/tag/library changes against independently captured native ownership. Keep document keys separate from project object IDs. | Native object lookup, missing/duplicate/stale association cases and complete affected-device scope. |
| Hardware dependencies | Add precise supported profiles for HSP/GSD, channels, vendor attributes, IO-Link, F-I/O and library references from public contracts plus genuine fixtures. Unknown catalog items remain unresolved. | Installed catalog/add-ins, product versions and relevant target hardware. |
| Native export | Build the versioned `CaxProvider` adapter on the engineering workstation, retaining complete result/log evidence and original exported bytes. Cover project/device/pruned differences separately. | SDK compilation, owned project exports, complete warnings/errors and reopen checks. |
| Native import | Prepare exact before/after scope, checkpoint, reservation and journal before enabling a mutation. Reconcile uncertain completion from native readback; never replay a timed-out import automatically. | Real import options/merge behavior, compile, save/reopen, interruption and verified full restoration. |
| Composite C02 | Bind exact document comparison to reviewed native connections/drives/technology-object mappings; reject ambiguous/stale mappings. | Whole-effect preservation and post-import compile/rebind/runtime verification. |

Native acceptance must include ordinary and pruned exchange, missing catalogs,
duplicate identities/names, import errors and warnings, address/channel changes,
unselected device/module preservation, protected objects, cancellation, process
loss and lost responses. Recovery must restore the whole affected project scope,
not just the selected device. Hardware behavior and safety acceptance are
additional gates. None is passed by this offline report, an XSD, a fake adapter
or a successful portable package.
