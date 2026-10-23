# D12 / X03 - Web applications and access configuration

Audit: 17 October 2026. Every feasible capability remains required for 1.0 under
[the release ledger](../release-obligations-20261015.md). No Siemens installation,
SDK, license, PLC, PLCSIM or UMC server is available here. This plan separates
public documentation, retained declarations, portable implementation and native
qualification. The original planning prompt is superseded by the user's explicit
instruction to implement everything feasible without Siemens.

## Current implementation and source audit

- `OpennessSession.ScratchSecurity.cs` configures new owned scratch CPUs only. Its
  DPAPI storage and exact-owner checks are patterns; its broad runtime role is not
  a monitoring-user template and must not be applied to existing CPU security.
- `Host/AuthenticationInstructions.cs` distinguishes credential authentication
  from certificate trust. Existing owned-empty-simulator first-use trust is not
  general trust or production certificate authentication.
- `Core/Security/AccessReview.cs`, `AccessWorkflowStore.cs`, `Host/AccessTools.cs`
  now provide supplied public grant comparison and immutable retained review.
  [Workflow](../../reference/access-review.md). The 73 managed and 42 actual MCP
  checks pass; combined qualification is recorded separately. No native adapter.
- `Core/Security/PublicCertificateReview.cs`, `PublicCertificateWorkflowStore.cs`
  and `Host/PublicCertificateTools.cs` now deliver strict public-certificate review
  with explicit UTC evaluation and declared store associations. [Workflow](../../reference/public-certificate-review.md).
  Targeted validation passes 96 managed and 68 actual MCP checks; native identity,
  trust and lifecycle remain unqualified.
- `Core/WebApplications/WebApplicationReview.cs`, `WebApplicationWorkflowStore.cs`
  and `Host/WebApplicationTools.cs` now deliver supplied configuration and selected
  ZIP-root comparison through existing bounded PackageStore extraction. Exact
  default files, unavailable phases, source-purpose separation and retained originals
  are covered by the [workflow](../../reference/web-application-review.md); native
  compatibility, generated DBs, deployment and endpoint acceptance remain open.
- Saved-review intake/artifact upload, exact nonvirtual project/hardware selection,
  content addressing, integrity revalidation and bounded paging are reused.
  Generic uploads retain bytes before review: these tools are not a secret vault
  or a redaction gateway. Only public metadata belongs in their input.
- `OpennessSession.ProjectModel.cs` captures hardware identity/navigation, not a
  complete access inventory, certificate association or app-deployment state.
- `Engineering/NativeTargetGuard`, prepared changes, engineering jobs and recovery
  journals provide identity/ownership foundations. A project checkpoint cannot
  alone restore changed external passwords, certificate trust or online assets.
- The retained [public inventory](api-coverage-inventory-20261014.json) includes
  286 heuristic D12 rows. The scan also includes unrelated HMI web controls; those
  belong to their HMI domain, not the CPU web-app adapter. Infrastructure rows do
  not prove absence of useful services. Installed accessor metadata is unavailable.

There is one primary checkout, no separate task branch/worktree or delegated
agent. Shared Host registration, contracts/catalog/bindings and aggregate/package
scripts are the integration boundaries. No live native work is scheduled.

## Documented capability matrix

Context7's three bounded D12 queries resolved only the official V20 index. Generated
REST examples were rejected; no `/api/webserver/configure` API is assumed. Official
V21 pages and retained public member IDs were counterchecked. Documentation code
contains inconsistent accessor names/capitalization; compile-tested public metadata
and target observation must settle those discrepancies before native admission.

| Workflow | Public route / source | Prerequisites, implementation and remaining proof |
| --- | --- | --- |
| Web configuration inventory | DeviceItem public attribute/service inventory; `DefaultWebPagesFeature`, `SystemWebPagesFeature` | Exact device/order/FW/security mode and activated interfaces. Capture absent/null/unsupported separately. Supplied project/hardware identity does not authenticate device metadata. Native general inventory absent. |
| Default page and app list | `WebApplicationConfiguration` Name, ApplicationType, IsDefault; [default-page docs](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/managing-web-application-configuration) | Name/type are read-only; default selection requires enabled web serving. Docs use a singular composition accessor; retained XML says `WebApplicationConfigurations`. No create/delete in that composition is established. |
| System pages | `SystemWebPagesFeature.EnableSystemWebPages`; [system pages](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/managing-system-web-page) | Documented S7-1500 FW4.0+ and web interface prerequisites. Offline activation does not establish external reachability. |
| Watch tables and display assignment | Public target attribute/service enumeration and D02 watch definitions; exact supported V21 fields not yet established | Inventory eligible tables, display/application references and their access scope. Preserve exact table/PLC identity and hidden/protected states. Do not infer support from a similarly named attribute; resolve metadata and qualify read/write visibility independently of watch acquisition or force access. |
| User-defined pages / generated DBs | `WebserverUserDefinedPages.GenerateBlocks` overloads; [generation](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/generating/deleting-blocks-for-user-defined-pages?contentId=OEtr3oGl5HxkqDxsZq97ug) | Existing-DB refusal versus explicit Override; directory/default file/app name and dynamic content/DB-number checks. Returned block identities define the affected set. Explicit overload inputs do not update project attributes. No bulk delete service is documented; generated blocks must not be hand-edited. Native generation and preservation remain open. |
| Online app download/update/delete | Download callback configurations `DownloadWebApplication`, `UpdateWebApplication`, `DeleteWebApplication`; [VoT download](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-downloading-data-to-plc-device/downloading-to-plc-with-vot?contentId=fAk3yK5GIT3NQhdf2DrERw) | Name and Checked select separate callback effects. A rename can involve download plus old-name deletion. Supported VoT/device/package prerequisites and target identity must be established. Offline page selection/generation is not deployment. No blanket acceptance of callbacks. |
| Legacy CPU web users | `WebserverUserManagement.WebserverUsers`, create/delete/password/permissions; [legacy service](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/accessing-web-server-and-opc-ua-user-management?contentId=DPgyuB30N_bUYASvB8xpIA) | Support is target-specific and writes require activated web serving. [V19 generation change](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/major-changes/major-changes-for-long-term-stability-in-tia-portal-openness-v19?contentId=Wj15qgGH67RLILE7m2Bxpw) replaces legacy service with centralized UMAC for newer firmware; do not universally route CPU users here. |
| SIWAREX web users | `SimpleWebserverUserManagement`, Active/UserName/Permissions/SetPassword; [SIWAREX](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/managing-webserver-user-of-siwarex-modules?contentId=ZgpZaCBTvs5MzK0caANhlQ) | Separate service for documented ET200SP WP341/WP351 modules; exact order/version required. Not a general CPU user composition; permission and activation semantics differ. |
| Project users, roles, anonymous access | `UmacConfigurator`, ProjectUsers, roles/function-right associations; [project users](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-data-of-the-user-management/functions-for-accessing-user-roles-and-function-rights/project-users?contentId=2eMNQCAsZDybxqly2DirDQ), [anonymous access](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-data-of-the-user-management/functions-for-umac-global-users-and-umc-server/accessing-anonymous-user-in-protected-project) | Engineering identities differ from runtime rights. Activation/authentication/alias/password and system/custom role changes require exact current state and administrator-preservation proof. Current supplied graph labels do not implement native semantics. |
| CPU/UMC access configuration | `PlcAccessControlConfigurationProvider`; [access configuration](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/accessing-plc-access-control-configuration-provider) | S7-1500 FW3.1+; extra UMC modes/attributes at FW4.0+. Server-ID trust and certificate trust differ. Attribute spelling/type discrepancies require SDK confirmation. No native writes implemented. |
| UMC users/groups and synchronization | `UmcServerConfigurator`, UmcServer lookup and project UmcUsers/Groups; [import user](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-data-of-the-user-management/functions-for-umac-global-users-and-umc-server/adding-an-umc-user-to-tia-portal?contentId=RaSbPMQe6yPvxCsBzVctNw), [activation](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-data-of-the-user-management/functions-for-umac-global-users-and-umc-server/activating/deactivating-umc-user-and-umc-user-group?contentId=4c6G7j0mO0bdsl3FCrXNHg) | Project-local representation, server identity, authentication and external state are separate. Imports, aliases, role bindings, activation, sync and stale membership need independent provenance and recovery. Full public member audit remains open. |
| Certificates | `LocalCertificateManager/LocalCertificateStore`, templates, create/import/export/delete and assignment; [certificate management](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/managing-certificate) | Bind store/owner, public DER identity, usage/SAN/time and exact association. Webserver and UA assignments are different attributes. Global-store switching is a separate migration, not initialization. Private-key presence is not recoverability; protected credential material stays out of reports. |
| OPC UA rights | UA submodule users, application/user roles and certificate associations; D04/D16 | Keep UA domain distinct even if account names match. Existing offline UA model review does not certify runtime access. Protocol negatives and exact certificate/model/program identity require later bench evidence. |
| Operational security | Retained X03 declarations: SyslogServerProvider, master secret, account lock, password policies, time/network/display/access features | Enumerate exact supported fields, units and side effects. Supplied graph does not cover these settings. Key reset, CPU protection and central-server operations require specific external-resource recovery. Full native API/format audit remains open. |

## Architecture and contracts

`access_review(reviewId, uploadRef)` and `access_review_page(recordId, section,
offset, count)` are implemented Host-only tools. Input has one exact saved owner,
one explicit security domain/source/version/mode, before/after public grant graphs,
selected subject and caller-defined required/allowed rights. Edges explicitly point
from an entity to the entity supplying inherited potential grants; they are not
native membership discovery. No translation between domains or name-only match.

The report retains original public observations and exact scoped grant differences.
Cycles, missing nodes, incomplete coverage, depth limits and disabled/unknown states
are explicit gaps. Potential grants include inactive declarations to prevent them
from hiding a privilege expansion; the tool never calculates effective native
permissions or proves safe removal. Original retained input is revalidated on reopen.
Errors do not echo forbidden values. The schema rejects credential/private-key fields,
but public identifier strings are not a content-classification or secret-scrubbing API.

Remaining contracts are **proposals**, not registered tools: native inventory and
capability capture in the owning worker; `prepare_change` kinds for exact settings,
role/grant edits, page selection/generated DBs and certificates; external-resource
journals for credentials/trust/deployment. Reuse current approval, identity and recovery
mechanisms rather than inventing a parallel mutation framework. Each preparation
binds captured product/FW/profile, exact owner, input hashes, before state and affected
consumers. Schema/product validation and current authorization precede dispatch.
No prepared artifact may contain a password, private key or reusable auth token.

App file review now consumes bounded original ZIP uploads, manifests selected-root
files and hashes, rejects traversal/links/duplicate/case-collision names, resolves
the exact default page and retains unsupported content without execution. It
compares the supplied application list, web/system-page declarations and opaque
generation properties; absent content and changed source purpose remain unknown.
Fresh extraction reuses PackageStore on reopen; released uploads are not required.
This does not establish Siemens package compatibility. Independent browser checks and actual target
package/generation validation remain separate. Public certificate review now parses bounded single DER/PEM inputs, reports exact
identity/time/usage, preserves opaque extensions and distinguishes declared
association from verified endpoint trust. Platform loader acceptance alone proved
insufficient: explicit checks reject extra content and extra ASN.1 wrapper fields.
Dedicated direct intake validates both phases before operation-owned retention; it
does not control client request logs or classify secrets hidden in public fields.

## Dependency-ordered work packages

Estimates are engineering days for one developer after required public metadata and
fixtures are available; native scheduling/licensing delays are excluded. All rows
remain release obligations until their full completion criteria are met.

| ID | Work / files and inputs | Completion criteria / dependencies | Estimate |
| --- | --- | --- | --- |
| WA-01 | Full API/target matrix in this plan and retained metadata; official docs | Exact signatures/setters/availability, firmware/security generations, dynamic attributes and genuine formats; no guessed app CRUD. Partial research delivered; installed metadata/captures open. | 2-4 |
| WA-02 | Core/Security, AccessWorkflowStore, Host, schema/tests; public observations | Supplied monitoring policy comparison, immutable restart/paging and refusal behavior delivered. Complete native capture and exact source binding after WA-01; fake graphs do not close native inventory. | 2-4 |
| WA-03 | Core public certificate inspection/review and artifact intake | Implemented bounded supplied DER/PEM review, exact public identity, explicit-time validity, usages/opaque extensions and declared associations. Container/extra payloads and malformed known structure refuse before operation-owned retention; targeted managed/MCP checks pass. Genuine/native store capture and assignment remain WA-08; this is not trust or full RFC policy validation. | 2-4 |
| WA-04 | Core app archive/manifest and configuration review | Supplied configuration, bounded ZIP manifest/default selection, opaque file preservation and restart-safe comparison are implemented through two Host tools. Managed/MCP malformed-input and retention checks pass. Genuine profiles/version limits and native package/generation/deployment compatibility remain open; requires WA-01 for Siemens compatibility. | 3-5 |
| WA-05 | Native settings/default/system-page adapter | Exact capability/rights gates, watch/display assignments where publicly supported, previews, compile where applicable, unchanged interfaces/apps, save/fresh reopen and verified checkpoint restoration. WA-01/02/04. | 3-6 |
| WA-06 | Native project/CPU/legacy/SIWAREX/UA role adapters | Full supported user/group/role/function-right lifecycle, alias/activation/credential-reference operations; separate family policies, no unintended privilege expansion or administrator lockout. WA-01/02/09. | 6-12 |
| WA-07 | User-page generation/deletion and impact review | Exact generated block ownership, collision/Override preview, preservation of all unrelated blocks, compile/reopen/recovery and retained source manifest. Do not edit generated payload internals. WA-04/05. | 4-7 |
| WA-08 | Certificate creation/import/export/renew/delete/assignment | Public fingerprints, store mode, dependency and endpoint/client migration proof; separately protected key recovery and rollback, expiry/usage/SAN negatives. WA-03/09 and D04/D16. | 5-9 |
| WA-09 | Protected credential reference + external-resource ledger | Generalize storage only with exact resource/owner/identity, ACL/DPAPI context and no secret output; model unknown outcomes, unreadable recovery and trust/key replacement. Project restore alone must not report success. | 4-8 |
| WA-10 | UMC import/sync/activation and operational settings | Server/project distinction, stale groups/roles, loss during sync, syslog/time/access/password policy effects and independent external-state restoration. WA-01/06/09. | 5-10 |
| WA-11 | Exact online web/UA/application handoff | Named callback authorization, app download/update/delete preservation, failed/partial deployment reconciliation, TLS/auth negative tests and physical endpoint proof. WA-05/07/08/09 plus D16/C03. | 5-10 |
| WA-12 | Full release integration / compatibility | Tool catalog, artifact schemas, install/update support, public docs, portable and native target matrix, scale, end-to-end monitoring/app scenarios. All above; no skipped native gate counts as passed. | 3-6 |

## Complete scenarios and acceptance

**Monitoring account:** capture the exact owner/security generation, all relevant
local/central/anonymous groups/roles and current public certificate associations.
Specify explicit scoped read/diagnostic rights, compare potential additions/removals,
and record unknown policy/identity gaps. The current supplied review supports this
review stage. Native completion requires authentication/authorization independent
of role names: intended reads succeed; writes, force, STOP/RUN, firmware, user/admin
changes and out-of-scope reads fail. Preserve other principals and recovery/admin
access, save/fresh reopen, restore prior configuration and verify independent login.
Never probe harmful write effects on production hardware; use a separately admitted
fixture and protocol-level refusals where supported.

**Application update:** retain previous package/source files, generated DB inventory,
default/system pages, rights/cert associations and exact current target identity.
Review bounded file deltas and source/deployment mapping. Generate only supported
DBs or use the documented package route; preview Override/collisions and exact
create/update/delete callbacks, including rename effects. Compile/read back, save and
freshly reopen; then separately authorize deployment, compare independent endpoint
content and preserve other applications. Project rollback and online rollback need
separate receipts, including lost reply/interruption and partial asset transfer.

Native/protocol qualification binds source tree, fixture/profile/product/FW, binaries,
inputs and before/after independent exports. Test unsupported services/FW, missing
rights, stale source/membership, wrong owner/domain, malformed package, untrusted/
expired/mismatched certificate, changed private-key availability, incomplete secret
recovery and lost responses before/after dispatch. A timeout retains unknown outcome;
no retry or ownership release until exact current state is reconciled. Reuse prior
accepted evidence only within its exact source/profile scope; no redundant VM run
is scheduled here. Native capture/edit/deployment must eventually prove complete
untouched-content preservation and recovery, not only requested-value equality.

## Phases, blockers and next executable work

Discovery/feasibility remains WA-01; supplied grant review is the first implemented
portable part of the monitoring workflow. WA-03 supplied public certificate review
is now implemented, as is WA-04 supplied archive/configuration review. Next without
Siemens: continue WA-09 native/provider handoff, authenticated target association and profile/context recovery design; operation ownership and one-use dispatch/recovery now extend the existing journal. Local protected references/secure input are implemented. Its portable external-resource
portion now extends the [shared engineering journal](../../reference/engineering-operation-journal.md#external-resources-wa-09-portable-portion): exact owner/resource reservations, separate baseline/readback proofs, unknown ownership and independent reconciliation. The [local protected credential store](../../reference/protected-credentials.md) now supplies context-bound references, restricted ACLs, DPAPI, secure local input, required lifecycle state and local retirement. No live provider consumes those references. The trusted Host pins dispatch and recovery generations before acquisition, refuses retirement while journal obligations remain unresolved, and consumes dispatch slots only after durable intent. Trusted recovery consumption now retains a reviewed and freshly reacquired same-owner session, permits changed post-mutation content, consumes only recovery slots once and still requires independent external reconciliation. Mapping an inaccessible or different owner, authenticated provider association and native acceptance remain open. These contracts do not replace native
capture/edit or the full supported expansion. Every native lifecycle and operational
security row stays open for release acceptance.

Unavailable prerequisites: current installed public metadata, genuine target exports,
versioned web package examples, exact product/FW entitlement, an admitted engineering
fixture, central UMC service and physical web/UA endpoints. Smallest later experiments:
read capabilities/attributes on one exact representative of each security generation;
verify singular/plural accessor via public assembly metadata; generate a minimal page
with independent before/after block export; enumerate named deployment callbacks;
replace a disposable certificate and prove both project and endpoint rollback.
No raw project internals, universal web-app CRUD, automatic full-access role or
assumed effective-rights engine is accepted as a fallback.
