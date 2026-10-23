# Runtime terminal-read oracle repair

Status: focused regression, actual packaged Host checks and final combined
aggregate/package validation pass. No Siemens, physical PLC or native SDK was used.

The HMI repair's first final package attempt failed at the credential-retirement
assertion. Its retained capture has two successful reads, a `CredentialRetired`
read, then a scheduling-gap row, ending with `sessionUnavailable`. The capture
writer deliberately accounts for elapsed gaps after a read and during finalization.
The assertion incorrectly treated the final row as the final read.

The shared harness helper now selects the last row whose kind is `read`. Both
credential-retirement and changed-handoff assertions use it. Their terminal-state
and no-further-RPC conditions remain unchanged. A later successful read or different
error cannot be hidden by an earlier expected error; a capture without reads refuses.
Four new regression tests and the six existing capture-contract tests pass. The
independent lifecycle verifier keeps its separate read-row filtering implementation.

The complete revised TLS/DPAPI/Host workflow passes 1,465 checks against the exact
Host/Core binaries of the failed relocated package. The independent verifier passes
seven retained capture chains, three original migration comparisons and seven
mutation controls. These counts describe this execution; polling check counts can
vary with timing. Original failed capture segments and the failed full package
result remain preserved under `artifacts/hmi-link-readback-01/fixed-package-source/`.
Focused rerun evidence is under `artifacts/hmi-link-readback-01/runtime-oracle-fixed-02/`.

This repairs a test oracle, not the production credential or capture behavior.
Native target identity, effective permissions and physical acquisition remain
unqualified. The original 44-workstream goal remains open.

Final combined source validation passes 44 managed suites, 1,518 Python tests
(one existing skip) and all 38 package stages. The final combined evidence is in
`artifacts/hmi-link-readback-01/verification.json`; native qualification remains open.
