# C02/C03 shared identity and evidence contracts

Status: implementation contract, 18 October 2026. The
[retained C02 workflow](../../reference/composite-engineering-review.md) now
implements exact retained selectors, explicit mappings and bounded declared
closure, durable publication/reopen and bounded Host paging/download/discovery.
Explicit v2 adds phase-local hardware, connection, TO and saved dependency
context with original provenance, shared visit/depth bounds and actual prior-v1
compatibility. The drive v2 and technology v4 supplied workflows now retain typed
telegram/encoder observations and exact same-review/phase dependencies. Explicit v3 composite selectors and cross-domain rules are implemented; complete
native consumers and native handoffs remain open. This contract fixes the
offline implementation boundary for C02 and C03 in the immutable
[44-workstream scope](../offline-scope-reconciliation-20261017.md); it does not
replace their original release obligations or complete either workstream.

## Existing evidence to reuse

| Domain | Existing integrity check | What it does not prove |
| --- | --- | --- |
| Saved engineering review | `ProjectReviewStore.Read(reviewId)`; review ID hashes the saved model, documents and capture | Atomic project revision, current project freshness or deployed program |
| AutomationML | `AutomationMlWorkflowStore` verifies its receipt, original documents and comparison inputs | AML IDs are native project object IDs; supported native import or complete impact |
| Connections | `ConnectionWorkflowStore.Page` rebuilds the report from retained input and saved review | Live endpoint ownership, transport capability, capacity or communication |
| Drive profiles | `DriveProfileWorkflowStore` revalidates exact input and captured hardware selection | Declared family/firmware, parameter access, units or references are native-qualified |
| Technology profiles | `TechnologyProfileWorkflowStore` revalidates input, saved review and retained sources | Live hardware/telegram/member compatibility or motion behavior |
| Runtime association | `RuntimeCaptureAssociations` checks explicit saved-case/signal declarations | `AssertedProgramIdentity` or equal types establish the running program |

Reuse these verification paths. Extract internal verified-load methods from the
existing stores where necessary; pagination and a new combined reader must use
the same verification logic. Do not parse arbitrary file paths or trust a
caller-supplied report in place of its retained original input.
Hashes and saved ancestry establish consistency of retained evidence; they do
not authenticate its producer or turn a supplied assertion into a live fact.

## C02: AML change to communication/drive/TO rebind review

### Identity decisions

Every selector is scoped by its immutable record reference and explicit
`before`/`after` side. A row name, display label, array index, network address or
equal value never establishes cross-record identity. Comparisons use exact
ordinal strings; no case-folding, path normalization, unit conversion or
numeric parameter-name synthesis is implicit.

| Selected entity | Required selector and verified context |
| --- | --- |
| AML object/interface | Exact original-document reference and report `Path`; retain `Kind`, `Scope`, optional `Id` and content hash. Paths identify positions in those exact bytes, not persistent native objects. |
| AML attribute/link | Exact document and attribute/link `Path`, with its declared owner and endpoint references. Missing/duplicate IDs remain explicit even when a document-local row is selectable. |
| Saved hardware/TO object | `reviewId`, nonvirtual `nodeId` and exact `objectPath`; all three must resolve to one row. Saved parent ancestry is evidence, not native ownership certification. |
| Connection endpoint | Connection record, side, exact supplied connection `id`, and `local`/`partner`; preserve origin, protocol, owner and interface paths. These row IDs are local to the supplied snapshot. |
| Drive parameter | Drive record, side, exact parameter `name`, declared `number` and `arrayIndex`; retain hardware selection and complete drive identity tuple. Equal numbers never override different names or parameter sets. |
| TO relationship | Technology record, side and relationship `id`; preserve kind, source/target paths, target member and declared/disconnected/unknown state. |

Mappings name both selectors, a bounded purpose (`hardware`, `communication`,
`telegram`, `encoder`, `parameter` or `dependent`) and a human-readable reason.
Purpose describes the proposed relationship; it cannot make unsupported row
kinds compatible. The first evaluator must explicitly enumerate supported
selector-pair rules. Unknown combinations are unresolved, never coerced into a
generic successful match. Telegram/encoder semantics require actual profile
metadata; a label containing either word is insufficient.

Mapping edges stay within one phase; a selector side that differs from that
phase is invalid. Separate before-to-after correspondences link resolved
selectors in the same domain and declare continuity or replacement with a
reason. Such a correspondence records a reviewer's claim, not verified identity
equality. It must preserve both original selectors and their independent checks.

Before and after have separate context bindings. Multiple component records
within one context must name the same saved review. A proposal may reuse one
saved review for both sides; a later independently captured result uses its own
review ID. Different IDs never imply equality or freshness. Retain each
review's host/project-path declaration and comparison consistency limits.
Different project-copy declarations require explicit review and cannot produce
a verified rebind. A future object absent from the saved model remains a
proposed unresolved destination; never manufacture a native identity for it.

Drive/TO family, firmware, technology version, options, parameter set and value
domain remain separate constraints. Existing profile reviewers reject identity
changes within a profile comparison. Composite code must preserve that guard:
a replacement needs independently retained profiles and an explicit migration
mapping, not a weakened same-profile comparison. Online observations cannot
authorize an offline edit, download, activation or motion command.

### Evaluation and retained result

1. Validate a bounded request and resolve every exact retained input before
   publication. Start with a 4 MiB request, at most 64 component records and
   4,096 mappings, depth 32, and strict duplicate/unknown-field rejection.
   Bound total resolved bytes to 128 MiB and report output to 32 MiB. Exceeding
   a bound is a refusal, not a silently truncated successful review.
2. Resolve selectors independently in each context. Retain unresolved,
   ambiguous, disconnected, cross-copy, profile-mismatched and unsupported
   states. A duplicate/conflicting mapping must not disappear through dictionary
   overwrite. One-to-many dependencies are explicit edges, not alias equality.
3. Evaluate changed AML declarations against mapped component rows. Report
   mapped impacts, unmapped changes, conflicting assignments and declared
   dependent closure separately. Missing rows are unknown unless the source's
   own complete-scope contract proves absence. Opaque changes retain a
   whole-document review requirement.
4. Follow only explicit verified-in-record references, with cycle detection,
   at most 128 hops and 10,000 visited edges. Exhaustion/cycles are visible
   coverage gaps. Never turn a graph bound into a complete impact verdict.
5. Publish the original request, dependency references/hashes, mapping outcomes,
   coverage gaps and report atomically using existing storage primitives.
   Reopen must revalidate original dependencies; changed/missing inputs refuse.
   Incomplete publication is discoverable and cannot be reused as a completed
   plan. Start with 512 MiB, 128 records and 1,024 filesystem entries; all limits
   include abandoned publications. Reuse dependencies by immutable reference
   rather than copying the complete input graph into every result.
6. Expose combined review, paged inspection and exact download through the Host
   and relocated offline package. Return `nativeQualified:false` and no native
   mutation authority. Do not call an import/rebind adapter from review/reopen.

Future evaluator expansion must preserve reopening of retained v1 records.
Version changed evaluation semantics and test actual prior-build records;
do not silently rebuild an old receipt with a different result algorithm.
The v2 consumer implementation shares the 10,000 edge visits with declared
impact and starts at its recorded witness depth within the 128-hop allowance.
Supplied links and saved dependency edges remain separate evidence categories;
their closure produces review candidates, never verified native effects.

The native continuation still needs supported AML import and actual network,
drive/telegram/TO rebinding; complete affected-peer capture; checkpoint and
reservation; compile applicability; save/fresh reopen; interruption and unknown
outcome reconciliation; exact recovery; and separate communication/motion proof.
A clean offline combined report establishes none of those native results.

## C03: security to physical-runtime handoff

Keep three independently sourced contexts: saved engineering declarations,
observed runtime identity/program, and observed authenticated-session permissions.
Do not promote a supplied certificate, credential-store binding, endpoint URL,
CPU label, asserted program string or successful connection into another kind
of evidence. Reuse D16's runtime session/capture lifecycle and the existing
credential/security stores; the offline OPC UA parser must stay network-free.

The handoff record must bind exact review/security/model references, target
identity evidence, deployment/program evidence, endpoint/security policy,
validated server certificate identity, client identity/credential reference,
session generation, requested operations and independently observed permission
outcomes. Store opaque protected credential references only, never secret
material. Absence of a supported program fingerprint remains `unknown`; no
invented checksum or upload request may fill that gap.

Match each constraint independently. Any mismatch, unknown required field,
expired observation, changed session generation, reconnect, program change,
certificate rotation or permission change invalidates admission and requires
fresh evidence. Wall-clock timestamps alone do not prove freshness. Permission
checks include both allowed and denied operations and scope; a successful read
does not authorize a write, mode change or download.

For the offline implementation, protocol fakes may supply explicitly synthetic
observations to exercise the evaluator and invalidation lifecycle. The
production path must remain unavailable until a real adapter supplies the
required evidence. Replaying a retained synthetic record can only reproduce its
review, never create a live session or grant authority. Runtime captures retain
quality, timestamp, sequence, overflow and gap information independently of
identity matching; bad or missing samples cannot pass assertions.

Certificate/client migration and rotation preserve the old/new evidence and
rollback conditions. A lost response after a mutation remains an unknown
outcome; no automatic replay or generic value rollback is permitted. Native
acceptance requires the exact CPU/program/security profile, real allowed/denied
access, broken-client migration, rotation/recovery and observation-quality tests.

## Ordered implementation and acceptance

1. C02 typed request/result schema and pure evaluator with independently
   constructed fixtures; retain all cases above without native authority.
2. Shared verified readers, bounded retained C02 store and actual stdio Host
   workflow; independent hash/selector oracle, restart/tamper/partial-publication
   tests and relocated-package coverage.
3. C03 evidence/permission evaluator and protocol-fake lifecycle integrated with
   existing runtime associations. Production admission remains unavailable where
   identity, program or permission evidence is missing.
4. Genuine supplied evidence and installed-product profiles, then separately
   qualified native adapters and complete end-to-end acceptance.

Required negatives include duplicate AML IDs, identical display names in two
PLCs, reused row IDs in different records, mismatched context sides, stale
review/report bytes, replaced drive/TO versions, ordinal parameter names that
look numeric, untyped telegram/member strings, conflicting many-to-one aliases,
opaque/unmapped changes, cycles and exhausted bounds. C03 additionally needs
same-address/wrong-CPU, same-CPU/changed-program, valid-certificate/wrong-endpoint,
expired/reconnected sessions, denied permissions, lost responses and bad/gapped
samples. These tests are required work, not executed evidence in this document.

## Typed C02 implementation checkpoint — 18 October 2026

The [v3 implementation and verification](../../verification/composite-engineering-typed-20261018.md) delivers exact telegram/address/encoder selectors, declared reference/domain/number constraints and separate encoder consumer context with actual prior-v1/v2 compatibility. Native eligibility and full effects remain unverified. Continue ordered item 3, C03 evidence/permission evaluation and protocol-fake lifecycle integration; all native gates and the full release scope remain open.

## Internal C03 implementation checkpoint — 18 October 2026

The [internal evaluator and full verification](../../verification/security-runtime-evaluator-20261018.md) delivers exact saved access/owner bindings, separate original target/program/session evidence, scoped permission comparisons and explicit invalidation without live authority. Actual prior access records retain their original bytes and behavior. Continue durable observation intake and MCP publication, then existing runtime/capture lifecycle and quality integration. The evaluator alone does not complete ordered item 3 or the full C03/native contract.

## Retained C03 implementation checkpoint — 18 October 2026

The [retained MCP workflow and full verification](../../verification/security-runtime-workflow-20261018.md) delivers original observation intake, exact review publication/reopen, bounded paging, downloads and discovery after interrupted or lost responses. The complete aggregate, relocated package and exact packaged-DLL probe pass. Continue existing runtime/capture lifecycle, independent quality/gap assessment and migration/recovery integration. This increment does not complete ordered item 3, the full C03 contract or native qualification.

## C03 capture binding checkpoint — 18 October 2026

The [capture integration and verification](../../verification/security-runtime-capture-20261018.md) binds original retained declarations to exact owned sessions before polling reads, preserves independent permissions/quality and historical exports, and passes full offline aggregate/package checks. Prior producer records remain unchanged. Bound in-flight crash/response-loss, credential/certificate migration and native adapter qualification remain open; this does not complete the full C03 or 44-workstream scope.

## C03 bound lifecycle checkpoint — 18 October 2026

The [bound lifecycle verification](../../verification/security-runtime-lifecycle-20261018.md) now covers actual read-response loss, an in-flight Host kill, credential retirement/replacement, same-endpoint server-certificate rotation and account replacement. Original capture chains and migration inputs pass independent checks after restart and package relocation. The shared HTTP boundary classifies response-stream I/O failures and disables TLS resumption so fresh pin checks observe certificate changes. This is local protocol/process evidence; actual device/program identity, native migration, client-certificate deployment, arbitrary scoped rights and mutation recovery qualification remain open. Continue C01/C04, broader D16 profiles/pressure and the full original scope.
