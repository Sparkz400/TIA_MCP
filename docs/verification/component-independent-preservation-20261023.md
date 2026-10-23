# Independent component verification repairs — 23 October 2026

The native component qualification preflight now rejects ten reproduced XML
identity/scope alterations even when their raw hashes are honestly updated.
It binds the direct Document/version/FB structure, FB composition and attribute
scope, and the complete observed native object-ID inventory across base and
oracle exports. This strengthens independent source checks; it does not prove
arbitrary native selectors or complete installed-content preservation.

The existing-caller preservation check also rejects twelve reproduced false
acceptances. It reads each caller snapshot once under the existing 4 MiB
component artifact bound and verifies its exact raw hash and byte count. It
preserves comments, processing instructions, QName value bindings, significant
whitespace and XML declarations. Networks must belong to the caller's direct
native ObjectList with distinct object IDs. Only the one newly added network
is removed before comparing the remaining payload through the shared native
comparison policy. Ordinary direct DocumentInfo export provenance remains
excluded; namespaced lookalikes remain part of the payload.

## Verification

The integrated source passes all **119 component Python tests** and contract
coverage for 261 tools, 56 mutation kinds and 335 catalog features. The tests
include 22 reproduced negative cases and controlled insertions into 16 retained
native FC/FB templates across FBD/LAD, software-unit and namespace profiles.
The added networks are synthetic; no native mutation was executed. Four other
matching filenames contain instance DBs and are explicitly excluded from the
code-caller template check. All original fixture bytes remain unchanged.

Eight selected historical component attempts were inspected; none claims passed
preservation. This is a scoped audit, not an exhaustive historical inventory.
Original failed attempts and baseline regression failures remain unchanged.

Integrated evidence: `artifacts/todo-component-independent-20261023/verification.json`.
Original reproductions and draft checks remain under
`artifacts/todo-component-oracle-draft-20261023` and
`artifacts/todo-component-preservation-draft-20261023`.

This increment follows the [completed full regression and package](todo-integration-previews-20261023.md).
That frozen package contains the earlier preview/decoder changes, not these
subsequent qualifier repairs. These Python-only corrections have focused checks;
they do not require another unchanged SDK build. Full native component
installation, preservation, recovery and runtime acceptance remain open.
