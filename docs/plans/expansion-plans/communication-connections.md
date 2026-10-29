# D08 communication connection engineering - 16 October 2026

All feasible connection families remain required by the [release ledger](../release-obligations-20261015.md).
This plan does not replace the complete D08 scope with subnet assignment or HMI
name matching. No Siemens product, SDK, simulator or PLC is installed here.

## Current implementation and source audit

- `OpennessSession.NetworkInspection.cs` reads configured network nodes, subnets,
  I/O controllers/connectors and exact owner associations. It does not enumerate
  general transport connections or prove communication or physical cabling.
- `OpennessSession.HmiBindings.cs` obtains `CommunicationManagement` on the exact
  HMI runtime owner, then uses `Connections`, `LocalTarget`, `PartnerTarget`,
  `LocalInterface`, `PartnerInterface`, `IsValid`, and HMI-specific
  `LocalConnectionName`/`AccessPoint`. Native object equality establishes owner
  associations; names alone cannot substitute for endpoints.
- `OpennessSession.ProjectModel.cs` captures hardware paths and selected details.
  A saved model alone is not a complete transport-connection inventory.
- `HardwareEngineeringRules.cs` verifies the existing plug/move/channel/address/
  distributed-I/O mutations. The audit reproduced a false preservation pass when
  non-object manifest rows were silently filtered. The shared comparator now
  rejects malformed or duplicate top-level identities before matching; missing
  selected paths produce structured failures. [Verification](../../verification/hardware-manifest-20261016.md).
- Existing HMI reference capture, project-review artifacts, prepared changes,
  exact source identities, journal/recovery and local artifact transport should
  be reused. A second connection ownership scheme would conflict with them.

The installed-API inventory described in the older roadmap is historical evidence,
not a current SDK installation. The [supplied connection review](../../reference/connection-review.md)
now delivers offline intake, explicit mapping, captured ancestry and declared
consumer comparison. Native read and create/set/delete routes are described under
Contracts and architecture below.

## Verified documentation and limits

| Source | What it establishes | What it does not establish |
| --- | --- | --- |
| [V21 release changes](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/what-s-new-in-tia-portal-openness) | V21 advertises creation/management for FDL, HMI, ISO, ISO-on-TCP, PtP, S7, TCP and UDP. | Exact accessor mutability, target availability, capacity, successful engineering or runtime. |
| [V21 connection concepts](https://docs.tia.siemens.cloud/r/en-us/v21/editing-devices-and-networks/configure-networks/communication-via-connections/connections-purpose-and-selection/introduction-to-configuring-connections?contentId=GsgBacisFnHVTAI_uzfikA) | A connection specifies partners, type, setup properties and communication path. | An exact Openness mutation contract or physical reachability. |
| [V21 open user communication](https://docs.tia.siemens.cloud/r/en-us/v21/editing-devices-and-networks/configure-networks/communication-via-connections/working-with-connections/using-open-user-communication-s7-1200-s7-1500-s7-1500t-s7-1200-g2/basics-of-open-user-communication-s7-1200-s7-1500-s7-1500t-s7-1200-g2?contentId=JzYUhI2~y5qYuGoIFdq5AA) | TCP/UDP/ISO-on-TCP have program-controlled connection descriptions; TCP-style establishment uses active/passive partners. | A single engineering-connection table covering all runtime-generated connections, or universal resource rules for every protocol. |
| [V21 Unified connection properties](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/connections/accessing-connection-properties) | `Node`, `Partner`, `Station` are read-only strings; `Name`, `DisabledAtStartup`, `CommunicationDriver`, `Comment`, `InitialAddress` are documented writable properties. | Those strings proving native ownership, or the same setter rules applying to hardware transport connections. |
| [Siemens NuGet reference package](https://www.nuget.org/packages/Siemens.Collaboration.Net.TiaPortal.Packages.Openness/21.0.1765349347) | References are resolved from `TiaPortalLocation` or the installed product's registry entry; no framework assets are included. | A standalone downloadable Siemens SDK that removes the local build prerequisite. |

Context7 was queried first (library resolution and two detailed queries, three
commands total). Its available V20 index returned rack/AML links rather than
V21 transport accessor details. Those generated snippets were not used to invent
API endpoints. Official V21 pages and existing source above are the current basis.

## Capability and compatibility matrix

| Family | Portable work still useful here | Native implementation / acceptance still required |
| --- | --- | --- |
| S7 | Exact local/partner review, retained configured IDs and user-code references; supplied before/after comparisons. | Confirm ID scope/ranges, CPU/CP/firmware, bilateral versus unspecified endpoint semantics, setter/recreation routes, permissions and communication. |
| TCP | Address/port/role representation and exact endpoint changes; profile-driven conflict tests. | Confirm native typed properties, wildcard/passive behavior, connection/resource pools, supported endpoints and data transfer. |
| UDP | Connection description and destination review without a TCP session assumption. | Confirm UDP-specific parameters, resources, addressing and datagram behavior. |
| ISO / ISO-on-TCP | Preserve raw addressing/TSAP representation, separate protocol identities and opaque fields. | Confirm target support, TSAP encoding/length rules, transport-specific APIs and runtime. |
| FDL / PtP | Model exact selected module/interface and unknown partner/resource facts; retain opaque protocol settings. | Protocol-specific modules, property schemas, setters, resource limits and physical bench. |
| HMI | Reuse native endpoint evidence and software connection references; inspect supplied properties and dependent tags. | Separate Classic/Unified/integrated profiles; verify native management versus software connection lifecycle, import/export eligibility, compile and runtime. |

A duplicate local ID or port is not universally a conflict. The exact resource
owner, ID pool, protocol, endpoint tuple, target version and sharing policy must
be established by a supported profile. Equal IP addresses across independent
networks likewise cannot establish ownership or a collision. Missing capability
facts remain unknown; do not promote them to a generic compatibility pass.

## Contracts and architecture

Delivered offline tools: `connection_review` and `connection_review_page`.
Native `connection_inspect` and the prepared kinds `connection_create`,
`connection_delete` and `connection_set` now exist on the existing prepared-change
lifecycle ([network inspection](../../guides/network-inspection.md#configured-communication-connections));
they are compiled against the V21 PublicAPI only and not natively qualified.

The delivered offline contract accepts explicitly supplied connection observations
bound to an exact saved review, with original bytes retained. Every connection
has explicit supplied identity, local/partner owner and interface references or an
unresolved reason, protocol, observed properties and coverage. Comparison must use
stable captured identifiers or an explicit reviewed mapping; names, addresses,
array positions and matching port numbers must never silently rebind a connection.
Keep engineering connection rows and program-generated connection descriptions as
separate origins. Preserve unknown fields as evidence without treating them as
supported setters.

A review may report declared changes, missing endpoints, ambiguous matching,
profile-known conflicts and affected captured consumers. It must retain unknown
resource capacity, incomplete consumers and unsupported properties, and must never
certify physical topology, native compilation or runtime communication. Upload,
immutable report, paging, download, restart, tamper refusal and exact source binding
must be delivered together. Resource validation is profile-specific; no fabricated
CPU capacity table or generic port-uniqueness rule is acceptable.

## Dependency-ordered implementation packages

| ID | Deliverable and completion evidence | Prerequisites |
| --- | --- | --- |
| CN01 contract inventory | Record exact public type/member/accessor metadata and genuine captured rows for all eight families, including unavailable getters and setters requiring recreation. | Public V21 metadata or supplied original evidence; existing historical member names alone are insufficient. |
| CN02 offline intake/review | [Delivered supplied-evidence workflow](../../reference/connection-review.md): retained inputs, explicit mappings, unresolved endpoint reports, opaque properties, before/after differences and declared consumer navigation. [Verification](../../verification/connection-review-20261016.md). Genuine captures and native property interpretation remain open. | CN01 for interpreted native properties; supplied-observation fields remain explicitly unqualified. |
| CN03 resource profiles | Encode independently sourced CPU/CP/firmware capacities, ID/address/TSAP/port rules, role applicability and recreation effects. Test duplicate, boundary, missing-fact and multi-interface cases. | Exact family/version sources; never infer limits from another family. |
| CN04 read adapter | Extend the exact native hardware scope with bounded connection discovery, endpoint equality, capabilities and separate coverage for each failure. Feed the existing project/HMI relationship model. | SDK build and a controlled capture; do not confuse generic enumeration with complete runtime connections. |
| CN05 lifecycle | Operation-specific previews; exact stale-source refusal; create/update/delete or explicit recreation; reservation/journal/checkpoint recovery; preserve peers and both endpoints. | CN03/04, supported writable accessor and lifecycle profiles. |
| CN06 integration | Carry changes into PLC instruction references, HMI tags/connections, ECAD/CAx and Explorer impact/navigation. Distinguish unresolved consumers from unused connections. | Existing typed/HMI/CAx contracts and native association evidence. |
| CN07 qualification | Native compile, independent readback, save/fresh reopen, recovery and lost-response reconciliation per profile; runtime/bench separately. | Installed products, correct CPU/CP/HMI versions, accounts/licenses and permitted target systems. |

Relative effort after prerequisites: CN01/02 are several engineering days each;
CN03/04/05 scale per protocol/target profile rather than per enum value. CN06 and
CN07 require their own integration/engineering windows. These are planning ranges,
not delivery promises; unavailable public metadata and original captures are the
critical current uncertainties. No parallel ownership conflict is currently known.

## Complete workflow specifications

### PLC-to-PLC

Select exact local/partner PLC hardware and interfaces in one captured project.
Choose a supported S7 or TCP profile explicitly; preserve each endpoint's observed
identity and any associated instruction/connection DB. Review a desired connection
with an explicit match or creation intent and known resource/role requirements.
The offline report must show both endpoints, changes, impacted captured consumers,
missing facts and relevant profile checks. Native execution later requires fresh
capabilities and unchanged source, one prepared operation, full affected-device
compilation, independent readback, peer preservation, save and fresh reopen.
A separate bench run proves the intended data transfer and reconnection behavior.

Negative fixture: two same-name partner CPUs on different hardware paths. Supply
a name-only or stale interface reference. The review must stay unresolved and the
native preparation must refuse; it must not pick the first equal name or address.

### PLC-to-HMI

Select the exact HMI runtime owner and native integrated connection, then establish
the PLC partner through the existing endpoint-equality provider. Preserve the
software connection's scoped link and all dependent tags. Preview the change with
Classic/Unified applicability and explicit unsupported properties; changing a
read-only partner string is never a substitute for changing the native endpoint.
Native application later verifies the supported lifecycle, full PLC/HMI compile,
unchanged unrelated connections/tags, save/fresh reopen and exact checkpoint recovery.
Runtime validation separately checks acquisition, displayed values and reconnect.

Negative fixture: two PLCs exposing the same tag and connection display names,
with one invalid or unobserved native partner. That row must remain unresolved;
no symbolic tag may be rebound through the other PLC's matching name.

## Qualification and unresolved decisions

For each family retain source, binary, profile, project and fixture hashes. Cover
multiple interfaces/controllers, disconnected and external partners, multilingual
names, missing/unreadable fields, duplicate identities, stale observations,
resource exhaustion, mutation response loss, interruption, fresh reopen and exact
unrelated-content preservation. A successful compile is not a runtime pass.
Recover unknown outcomes before permitting any dependent write.

Mutable versus recreate-only properties, scope of connection identifiers, exact
resource pools, exported document coverage and safe preservation of program/HMI
references remain unproven for the general native adapter. CN02 now supplies the retained
offline comparison workflow while leaving property/resource semantics unassessed.
Continue CN01/CN03 and real consumer integration before native mutation.
The complete D08 and [44-stream queue](../../history/offline-execution-20261016.md) remain open.
