# Runtime capture security handoff — 18 October 2026

Status: full offline aggregate/package validation passed; integration is recorded in
`artifacts/security-runtime-capture-01/integration.json` when pushed.
Base: `821bedc942ea3d8d814408ef58a7df22fd998801`. No Siemens/native execution.
The full 44-workstream goal remains active.

Optional `handoffRef` binds a capture to original retained C03 evidence and the
actual owned Web session. Start and each read check original dependencies and
session identity; fresh permissions and metadata still gate every value read.
The nested binding is versioned, with all identity/rights/authenticity/admission
claims false. Historical captures and reports stay readable; new analysis
requires intact handoff evidence. Export and Explorer validate the new shape
and show its limitations independently of quality and unresolved hypotheses.

## Focused evidence

Evidence root: `artifacts/security-runtime-capture-01/`.

- `managed-01.log`: 147 access, 268 retained-handoff and 40 initial capture
  binding checks. The final validated frozen runs pass 42 capture checks, including
  both retained writer-refusal records.
- `diagnostics-01.log`: diagnostic suite passes, including handoff export,
  missing flags, forged authority, mismatched contexts and escaped display text.
- `host-build-03.log`: Release Host build, zero warnings/errors.
- `runtime-mcp-05/verification.json`: 941 checks with actual local TLS, DPAPI,
  MCP Host and restart. New cases cover matching bindings, independent owners,
  provider failure, denied effective read permission, gaps, changed program
  declarations, tampered originals, repeated-start observation and reconnect
  refusal. Check counts include variable polling and artifact-secret scans.
- `browser-01/`: actual Chromium fixture run passes with no page errors/network
  requests. Handoff view inspected at 1280×800; hostile text and invalid binding
  cases preserve the previously accepted view. This is fixture UI evidence.
- Six capture schema tests pass, including new error records and false flags.
- `compatibility-01/verification.json`: 62 checks across two new Host lifetimes
  preserve three actual prior-producer records and 18 original files. The
  `fixtures/physical-runtime/retained-v1/fixture.json` manifest retains original
  producer DLL/source-manifest hashes, exact prior statuses/pages and export
  digests. No old records were regenerated with the new producer.

## Failures preserved and corrected

1. `runtime-mcp-01.log`: the new scenario reused a session that the preceding
   cancellation test could invalidate. It now explicitly reconnects.
2. `runtime-mcp-02.log`: the test incorrectly assumed two reads were immediately
   committed. The mutation scenario now uses single-signal cycles separated
   by two seconds and waits for the published prefix.
3. `runtime-mcp-03.log`: the new refusal codes were absent from capture-row
   validation. Tampering correctly prevented a network read but incorrectly
   became a storage failure. Writer/schema allowlists now retain the explicit
   refusal; runs 04 and 05 pass the actual Host regression.
4. The first frozen package run fails in the independent original-input checker:
   valid virtual model nodes need not have `objectPath`. Exact owner counting now
   ignores absent paths, preserving the unique exact-owner requirement. The
   original failing handoff passes the corrected checker.
5. Comparing packaged and browser-tested assets exposed a stale checked-in
   `index.html`. Both the new deterministic pack test and packaged-asset guard
   fail on the old page (`stale-viewer-negative.log`,
   `stale-package-negative.log`). The page is regenerated; source/entry-point
   drift and incomplete asset manifests are now regression-tested. Line-ending
   normalization alone did not explain the original mismatch.

The first frozen run remains preserved. The final runs below include the
oracle repair, current prebuilt viewer and package/source staleness checks.

6. The corrected aggregate exposed a shared Windows identity failure at a
   262-character component journal path. `long-path-negative.log` reproduces it
   directly in the engineering test executable. Native lookup now uses the
   documented extended-length form after the existing local-path/link checks;
   caller-supplied device namespaces stay rejected. `long-path-positive.log`
   covers an existing file over 320 characters, case alias, missing descendant
   and rejected namespaces. `component-long-path-positive.log` passes all 78
   component-window checks at the original failing temporary-root depth.
   See [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew).
   The corrected package passed 38 stages before this repair; it is not final
   evidence for the updated Core. The final aggregate and package below include
   the repair and pass the long-path regression.

## Final verification and remaining scope

Both final validated source snapshots contain 2336 identical files. The
aggregate passes 44 managed suites, 242 general managed checks, 1,510 Python
tests (one existing skip) and 25 Explorer model/client checks. All 38 package
stages pass; the exact 1,668-file package and relocated copy share manifest
`574950e106d7b49eaf4686329e33451a945848a4adc7ef02d4886a39dfc510bf`.

Runtime MCP passes 945 checks in aggregate and 938 in the package;
polling/file-scan counts vary, while the same fixed binding/refusal/restart
scenarios are required by the final verifier. Both runs include 42 managed
capture binding checks and 62 actual prior-capture compatibility checks.
The independent original-input evaluator verifies the new bound handoff too.

`final-packaged-core-probe/verification.json` records exact packaged DLL execution:
147 access/evaluator, 268 retained-workflow and 42 capture checks, with six
publisher interruptions. Its engineering apphost also passes all five long-path
regression boundaries with the exact packaged Core. Independent original-byte/report checks also pass.
`browser-03/` passes 37 actual-browser checks; all five browser-tested assets
match the package. The handoff view was inspected at 1280×800.

Each `ci-final-runtime-retention-*.json` verifies an isolated copy selected by the
workflow's upload patterns: five bound captures, eleven original handoff
records and their source dependencies. No TLS private keys are selected.
Bindings retain all 112 historical evidence rows unchanged; 106 remain missing.
See `verification.json` for source-manifest hashes and final evidence references.
Only final documentation differs from the validated snapshots.

Bound in-flight crash/lost-response and credential/certificate migration,
broader storage/resource pressure and native adapter qualification remain open.
No synchronous polling path establishes scan timing, actual program identity,
effective arbitrary scoped permissions or native behavior. Hosted CI remains
separate from local tests and was billing-blocked at the integrated base.
