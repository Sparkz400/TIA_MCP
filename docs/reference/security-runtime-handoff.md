# Security/runtime handoff evidence

The initial C03 evaluator compares separately retained public device, program and
session observations against an exact saved review and access-review record.
Six offline MCP tools retain observations and publish/reopen reviews. They do
not provide a native admission path.
The [contract](../../contracts/runtime-handoff.v1.schema.json) defines request,
role-specific observations and report shapes. Existing access tools and retained
records keep their original behavior.

## Retained MCP workflow

1. Upload each public target/program/session observation with the existing
   `artifact_upload_begin/chunk/commit` tools. Call `runtime_handoff_observation`
   with its `role` and committed `uploadRef`. Retain the returned canonical
   artifact path/digest. Exact bytes are copied independently of the upload;
   an exact-byte retry returns the same observation reference.
2. Create a `runtime-handoff-request.v1` using those references for each of the
   baseline/current roles and exact saved review/access/owner references. Upload
   the request and call `runtime_handoff_review`. The request is retained byte
   for byte without rewriting its references. Each invocation creates a new
   immutable record; it is not an idempotent native operation.
3. Keep the returned `recordRef`. `runtime_handoff_reopen` checks the original
   intent, receipt, request, report, all six observation references and saved
   access/model dependencies, rebuilding the report before returning a summary.
4. Use `runtime_handoff_page` for `constraints`, `permissions`,
   `baselineObservations` or `currentObservations`. Pages contain 1..200 rows
   within a 128 KiB structured result. Oversized observations are explicitly
   omitted from the page, never silently dropped from the report.
5. `runtime_handoff_artifact_read` downloads `request.json`, `report.json`,
   `receipt.json`, or an exact `baseline.target.json`, `baseline.program.json`,
   `baseline.session.json`, `current.target.json`, `current.program.json` or
   `current.session.json`. Chunks contain 1..65536 requested bytes, exact offsets,
   total length and SHA-256. Each call revalidates the complete evidence chain.
6. `runtime_handoff_list` discovers complete, incomplete and verification-failed
   records, plus retained/pending observation-file counts. It never completes an
   interrupted publication. After a lost response, inspect the returned handles
   and their original requests instead of automatically replaying the request.
   An exact observation intake can be retried; an unknown native mutation cannot.

The store keeps canonical original observations separately from immutable review
records. Original uploads may be released after successful retention. Saved
access/model dependencies and retained observation files must remain unchanged.
Publication flushes intent, request and report before its final receipt. A
receipt is accepted only when its original artifacts and rebuilt report agree.
Rehashed output claims do not bypass that comparison. Storage is bounded to
512 MiB, 128 review records and 2048 entries, including pending files. Existing
exact records remain readable over quota; new publication refuses. Links and
junctions are refused. All tool paths operate without network or native workers.

`RuntimeHandoffReview.Build(artifactsRoot, bytes)` reads a bounded request and
reopens every original dependency. `accessRecordId`, `reviewId`, `accessPhase`
and the owner's exact node ID/path select the saved declaration. Owner ID and
path must each be unique. The shared access reader rechecks the original public
input against the saved review before its report can be used.

`expected` carries the original Host ID and `WebRuntimeContext` (endpoint,
connection ID, epoch and peer certificate digest), declared target/program
identities, subject/client identity, optional opaque protected credential
reference, security domain and mode. The selected access review must agree on
owner, subject, domain and mode. Only the existing Web-context domains
`cpuRuntime` and `legacyWeb` are covered. This does not translate OPC UA,
project UMAC or SIWAREX rights into Web permissions.

An optional `plcsimBinding` selects the path/digest of a `runtime_plcsim_bind`
record. The review then matches target identity against the bound owned PLCSIM
instance (`plcsim-owned-instance`, name and marker digest) instead of the
supplied `targetIdentity`. Program identity is matched against the bound online
fingerprint (`plcsim-online-fingerprint-sha256`) when one was recorded. The
expected endpoint must be one of the binding's controller addresses, and
`binding` constraints compare the supplied expectations with the bound identity.
The report's `targetBinding` and the receipt state `boundOwnedPlcsim`. With
`plcsimBinding: null` the report states `unbound`. Requests without the field
keep their original report bytes, and their receipt states `unbound`.

`baseline` and `current` each name three exact artifact path/digest references:
`target`, `program` and `session`. Every original has its own role-specific
schema, `supplied` or `synthetic` provenance label, Host/context binding,
nonnegative Int64 sequence and declared `current`, `expired` or `unknown`
freshness. Target and program identities use opaque scheme/value pairs, or
null for unknown. No address, certificate, label or metadata hash substitutes
for a missing program identity. Separate files do not authenticate independent
producers.

Session evidence retains subject/client/credential, security domain/mode,
declared peer/authentication state, session state and explicit scoped permission
outcomes. Requests select 1..64 operations with an expected `allowed` or `denied`
outcome. Each must have exact ID/scope evidence in both observations. Public
grant closure is reported separately; it never supplies a missing permission.
A successful read cannot imply a permitted write, mode change or download.

The report gives separate constraints for each role/context and identity,
both phase permission outcomes and baseline/current changes. Mismatch, unknown
required evidence, expiry, regressed sequence, changed permissions or a changed
payload at the same sequence requires fresh evidence. An unchanged observation
is reproducible, but its replay cannot create a session. Wall-clock timestamps
are not used to assert freshness. Sequence and freshness themselves remain
supplied declarations until a qualified live observer is integrated.

All reports have false native execution/qualification, authenticity, freshness,
device/program verification, effective-rights, operation-admission and
assertion-admission flags. Even `consistentDeclarations` has admission
`unavailableWithoutQualifiedNativeEvidence`. Mutation recovery explicitly
requires independent reconciliation of an unknown outcome, without automatic
replay. This evaluator does not execute mutations or perform rollback.

Bounds are 128 KiB for the request, 256 KiB per original observation, six
observation references, 256 permission rows per session, 64 MiB of charged
request/reference bytes and 2 MiB for the report. Duplicate JSON fields,
unexpected fields, duplicate scoped permissions and duplicate requested
operations are refused. Inputs are public metadata only; validation is not a
general secret-content detector.

The optional [capture binding](runtime-captures.md#optional-retained-security-handoff)
now connects a retained review to the exact owned runtime session. Start and
each read revalidate original evidence; later permission checks and sample
quality remain independent. Session and capture Host IDs retain their different
owners. Historical exports do not refresh or authenticate declarations.

The [capture integration record](../verification/security-runtime-capture-20261018.md)
and [bound lifecycle record](../verification/security-runtime-lifecycle-20261018.md)
record the exact full aggregate/package validation scope. The lifecycle harness
retains exact interrupted prefixes, lost-response errors and old/new credential,
certificate and account evidence. Restoring a previous certificate does not
resume an ended capture. Broader resource/storage failures, genuine migration
profiles and native-adapter prerequisites remain open. Native CPU,
program, client/security migration and effective-rights qualification remain
unavailable without the corresponding products and target evidence.
