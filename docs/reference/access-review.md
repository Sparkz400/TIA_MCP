# Supplied public access review

`access_review` compares public grant observations or an explicit proposal without
Siemens installed. This is this project's observation format, not a Siemens export
or native permission engine. It does not modify accounts or calculate effective
permissions.

1. Import/reuse a saved review and select exact nonvirtual `nodeId` and `objectPath`.
   `projectUmac` uses a project owner; `cpuRuntime`, `legacyWeb`, `siwarexWeb` and
   `opcUa` use a hardware owner. These are separate supplied domains, not native
   service availability assertions.
2. Prepare the strict [request](../../contracts/access-review.v1.schema.json), with
   `identity` repeated unchanged in `before` and `after`. Supply public `sourceId`,
   `version`, `securityMode` and an `unknownReason` if version/mode is unknown.
3. Supply the exact `subjectId`, `requiredRights` and `allowedRights`. Each right is
   `{ "id": "ReadTag", "scope": "cpu:A" }`; these example labels have no built-in
   Siemens meaning. Every required right must also be allowed at the exact scope.
4. Upload with `artifact_upload_begin/chunk/commit`, then call
   `access_review(reviewId, uploadRef)`. Retain `recordId`, `input` and `reportArtifact`.
5. Call `access_review_page(recordId, section, offset, count)` with `summary`,
   `observations`, `assessments` or `changes`; download complete artifacts using
   `artifact_read`. Released uploads and Host restarts preserve the retained review.

Each phase has `entitiesComplete` and up to 512 entities, with exact `id`, `kind`
(`user`, `group`, `role`), nullable Boolean `enabled`, `rightsComplete`,
`assignmentsComplete`, `rights` and `inherits`. An `inherits` edge points from the
entity to a source of potential grants: user -> group -> role, for example. This is
an explicitly supplied graph, not an inferred native hierarchy. IDs are unique
within a phase; display names are never used for matching. Role-kind changes remain visible; owner changes require a separate exact review. Different security identities/domains cannot be combined.

The two assessments retain visited entities, potential rights, rights outside the
caller-defined allowlist, required rights not observed, and explicit gaps. Reaching
a complete supplied closure is not complete native inventory. Cycles, missing
entities, unknown activation and incomplete rights/assignments remain gaps. Disabled
entities still contribute potential grants so a declaration cannot conceal them;
no native activation/deny/precedence semantics are inferred. Equal right names on
another scope never match, and unrelated roles never contribute to the subject.
Only-before/after rows are observations, not proof that an account was deleted or
created. Missing required rights are not proof of denial. Zero extra rights is not
minimum-rights certification or safe removal of administrator access.

Inputs are bounded to 2 MiB, 512 entities/phase and 64 rights or inheritance links
per row; traversal depth is 64. Reports are bounded to 16 MiB, retained storage to
512 MiB and pages to 128 KiB / 200 rows. Oversized page rows explicitly point to the
complete artifact. Exact original input and saved review are revalidated on every
reopen; forged reports cannot upgrade authorization or qualification.

Use **public metadata only**. Passwords, private keys, tokens and arbitrary settings
are not accepted fields. Unknown-field errors do not echo supplied values, but
ordinary string identifiers are not a secret detector. Generic upload storage
retains bytes before this tool validates them; do not upload secrets expecting this
review to redact or erase them. The output is not a credential vault, protected
reference service or deployment approval.

The [full D12/X03 plan](../plans/expansion-plans/web-server-access-configuration.md)
now includes [public certificate inspection](public-certificate-review.md). Native
capture/lifecycle, app/configuration review, credential recovery, UMC synchronization,
operational settings and separate physical web/UA acceptance remain unfinished.
