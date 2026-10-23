# Test Suite exact identity text - 16 October 2026

Base: `93c035ff551d8f52ab34c8e30d8af2f3f79f97e3` (AutomationML workflow).
That base passes hosted runs `38944550539` (Python, managed, package and isolated OPC UA) and `38944547821` (Explorer).
This increment repairs offline identity validation. No Siemens installation,
native worker, PLC, PLCSIM, licensed test execution or qualification is involved.

## Reproduced defect and repair

Before the fix, a dependency ID containing an unpaired UTF-16 surrogate passed
capture validation. JSON serialization replaced code unit 55296 with 65533;
the planner then resolved the replacement to a different, valid definition and
reported no blockers. `artifacts/testsuite-text-dev/repro-before.log` retains
the before result; `artifacts/testsuite-text-before.log` retains the failing regression.

Capture now uses the existing planner text guard before serialization. Dependency
and unresolved-syntax entries, execution profiles, idempotency keys and project
session epochs reject malformed Unicode, controls and invalid XML characters.
Valid Unicode is unchanged, including emoji, literal replacement characters and
separately composed/decomposed identifiers. Null captures refuse explicitly.
Retained reviews and job keys receive the same boundary checks.

Core strict artifact parsing also decodes every JSON string/property name before
deserialization and translates malformed escaped UTF-16 into
`InvalidTestSuiteArtifact`. Valid raw diagnostic controls and surrogate pairs
remain supported. `artifacts/testsuite-text-dev/json-before.log` records the
previous unhandled property-name exception.

Actual MCP transport has a separate boundary: the installed SDK rejects malformed
escaped UTF-16 before the tool is invoked, returning RPC error `-32603`.
`artifacts/testsuite-text-dev/mcp-surrogate-probe-02.json` retains the stack and
clean process exit. This increment does not change that SDK error classification.
The actual Host regression checks refusal, continued usability and unchanged
retained file hashes; control/noncharacter selector inputs receive explicit tool
errors. No malformed metadata can create a review or queued operation in these
cases.

## Executed checks

Development source: `artifacts/testsuite-text-dev/after-03.log` passes 549 managed
checks, including 48 exact-text/parser checks and existing scale, result, queue,
recovery and scope coverage. `mcp-03.json` passes 46 checks across four actual Host
lifetimes. All four exits are zero and unforced, with no native worker exits.
The Host build has zero warnings/errors.

Final frozen source: `artifacts/testsuite-identity-text-01/source`, tree
`46bd7f285ba98d10da8d138fbe53bed8dbee78d3`, 1820 files. The full aggregate
passes all 30 managed suites, 1,280 Python tests (one unavailable-native-evidence
skip), 24 Explorer model/client checks and every portable MCP workflow. The
Test Suite slice passes 549 managed checks and 46 actual MCP checks across four
zero-exit, unforced Host lifetimes; no native worker starts. Schema checks cover
two valid documents, nine rejected adversaries and ten catalog/D06 mappings.

| Retained receipt | SHA-256 |
| --- | --- |
| `artifacts/testsuite-identity-text-01/source-manifest.json` | `4d6df182032f6c57ba4db937d655fc38b2b3dfa89843cf53821c600b7080a446` |
| `artifacts/testsuite-identity-text-01/source/artifacts/checks/result.json` | `ff8cf16075bf6534046d33b4e780ef0af22834209dc4429b4949d1f4e7c4fa81` |
| `artifacts/testsuite-identity-text-01/source/artifacts/checks/testsuite-mcp.json` | `b40fa00a022f24ddb6d293dd748fe6391c9ec5a781277a1755d8e5157145660b` |
| `artifacts/testsuite-identity-text-01/source/artifacts/checks/testsuite-schema.json` | `8dba954f723e0c6ed00e197371f05e9fd529a99e3fe4218ad706a343b893b7cd` |

Implementation, contracts, tests and harness bytes match this frozen source. Only
documentation was updated afterward to record results, the historical/current
workstation distinction and the next reproduced diagnostic defect. The 106 absent
historical evidence files remain unverified; six available files match their
retained hashes.

## Hosted CI availability

Implementation commit `32e52d9d6f0a4c29f910abb4c62712681dd4220b` reached
`origin/main`. Both [offline CI](https://github.com/Sparkz400/TIA_MCP/actions/runs/38947656909)
and [Explorer CI](https://github.com/Sparkz400/TIA_MCP/actions/runs/38947656835)
ended in failure before any test step started. GitHub's check annotations cite
failed recent account payments or a spending limit needing increase. This is an
account-level execution refusal, not an executed test failure. The exact run,
empty step lists and annotations are retained in
`artifacts/testsuite-identity-text-01/hosted-ci-refusal.json`.

The local frozen aggregate and fallback portable package/relocation run pass.
Browser rendering and Siemens/native qualification were not run in this
increment. The refusal receipt SHA-256 is `b528a79c89b583c15fbcdfb75f2a85087e2eada067e0a0c5f712aefa846b42d4`. Billing settings
were not changed and refused jobs were not repeatedly rerun. After the account
blocker is resolved, run both workflows on the then-current main commit; local
Windows checks do not establish hosted Windows or Ubuntu acceptance.

## Local portable package fallback

The package was built from the same frozen source after GitHub refused to run.
Deployment guards, exact ZIP/hash/license/configuration checks, original and
Unicode-path relocation, and packaged GRAPH, AutomationML, OPC UA, VCI,
translation and diagnostic/measurement workflows all pass. The isolated package
build reports zero warnings/errors. Native workers remain disabled in the bundle.

Bundle: `artifacts/testsuite-identity-text-01/source/artifacts/releases/tia-mcp-offline_20261016_183004_27fc1dd5.zip`.
ZIP SHA-256: `f636b15d3dad25f08753e054e1c9a4c9a64927decef469ef7df444481d4e6f75`.
Bundle manifest SHA-256: `a791575ab34fd5d4ded82ad8f94a67055453f0d0c3bb39b881fce162644cd5f1`.
Package result: `artifacts/testsuite-identity-text-01/source/artifacts/package-checks/result.json`,
SHA-256 `fea4effa0c7c0b72f70388a5193274ae03e1bf4bfa6fdd85ca008230c5d48732`.

This proves local Windows package consistency and exercised workflows. It does
not replace hosted Windows/Ubuntu execution, a new browser-rendering run,
publisher signing, destination acceptance or any native qualification.

## Remaining work

D06 remains open: broader result/ancestry diagnostics and provider-contract audit
continue in the full offline queue. A separate pure reproduction at
`artifacts/testsuite-findings-dev/before.log` confirms that a message labeled
`Success` with a positive error/warning counter fails or makes coverage
inconclusive in the normalizer, yet disappears from `Findings`. That diagnostic
projection defect is not changed by this identity-text increment and is next in
the D06 repair queue. The raw report and failing coverage gate remain intact.

Actual native ancestry, current scope/license providers, SDK build, execution/recovery and product qualification require an
appropriate Siemens installation. Pure supplied readback comparisons do not
authenticate a native observation or qualify production execution.
