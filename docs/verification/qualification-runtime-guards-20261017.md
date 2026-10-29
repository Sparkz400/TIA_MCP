# Native-evidence runtime guards — 17 October 2026

Base: `fc168e370b68ac745e682eaf8810c269aa98f38d`.
No Siemens SDK, product, simulator or PLC is installed here.

## Reproduced defect and repair

Python removes assert statements under optimization. Running the existing connected
encoder qualification tests with `-O` reproduced 15 failed negative controls across
13 tests: changed XML digests, false completeness, disconnected/wrong endpoints,
unintended parameter changes and old display-only channel summaries could pass.
Original failure log (`artifacts/encoder-optimized-before.log`).

Five connected harness modules now use explicit runtime validation:
qualify_encoder_hardware, verify_encoder_preservation, audit_encoder_preservation,
audit_engineering_preservation and engineering_preservation. All 130 original
conditions and explicit error messages are retained. A shared require helper raises
AssertionError regardless of interpreter optimization. This preserves existing
exception/recovery behavior and prevents optimization from skipping calls inside
checks. No native operation or new evidence acceptance is enabled.

The affected checks include retained-artifact path/hash boundaries, exact XML and
parameter preservation, expected encoder edit construction, dependency comparisons,
compile/restore states, owned fixture paths, process-exit proof, safe retry conditions,
fresh-session evidence and independent audit outcomes. Existing historical reports
remain unchanged. Offline validation does not authenticate the producer of supplied
native evidence or restore missing receipts.

## Executed checks

The repaired optimized encoder tests (`artifacts/encoder-optimized-after-01.log`)
pass all 13 cases. Optimized general preservation tests (`artifacts/preservation-optimized-after-01.log`)
pass all 13 cases. Eleven new guard cases (`artifacts/native-evidence-guards-01.log`)
exercise retained hashes/boundaries, report-versus-file mismatches, a complete synthetic
encoder edit/recovery audit, collateral XML changes despite matching hashes, failed
compilation/recovery, empty audits, mismatched companions and fake Windows process
results. A fake Host confirms a failed scratch compile cannot reach hardware work;
a failed retained compile cannot start the verifier Host. These fakes never launch
Siemens or invoke Win32 process operations.

The integrated optimization regression (`artifacts/native-evidence-optimization-01.log`)
executes all 37 focused cases in separate Python processes under both `-O` and `-OO`.
The normal aggregate includes this regression, so optimization behavior remains checked.
The synthetic positive audit explicitly reports saved-copy evidence unavailable.

Frozen Git tree: `1798136a3ba87dc6c3c77544dc3cc725d90f3c62` (1978 files).
Source manifest (`artifacts/qual-guards-01/source-manifest.json`) SHA-256:
`110cc6297e89dfa72a4fb9dbf515c74def416f8e71d1af9055a62c92cb7313d6`.
Source ZIP SHA-256: `924941b99b0b020fec9450721a3fab93dcbccfdce6a5b2e744e633ae8be25a08`.

The aggregate (`artifacts/qual-guards-01/source/artifacts/checks/result.json`)
passes 39 managed suites, 1,321 Python tests (one unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all offline MCP workflows.
The unchanged general executable passes all 202 checks; the Python aggregate includes
the new runtime-guard and optimization regressions.
Run: 2026-10-17T07:43:12.7545344Z to 2026-10-17T07:53:42.2592818Z.
Receipt SHA-256: `817102e3a95bcfb47807955201cf0da6a18073fb63d7f76f0ac929e904276e25`.
Console evidence (`artifacts/qual-guards-01/aggregate-console.log`).

All 22 package stages (`artifacts/qual-guards-01/source/artifacts/package-checks/result.json`)
pass, including relocation. Bundle `tia-mcp-offline_20261017_074336_52d4ea5f` contains 1396
manifest files. Receipt SHA-256: `16e5c1f1ed8d8fac1c692b70b402e79dd11d5bafa6afa63e4781b661857aab4e`.
Manifest SHA-256: `289156275bb3f54a5684489a63a9b345f1777bdc52d5639796b63dc3c255e868`.
ZIP SHA-256: `f354614ba0652887cfa7f4bd319bcc23355303ddf6042a59f815d62f8053d822`.
This offline package excludes native workers; it does not compile the Siemens SDK.
Hashes establish retained byte integrity, not publisher or native authenticity.

The source comparison (`artifacts/qual-guards-01/final-source-comparison.json`)
allows only completion-documentation changes after testing and verifies all 106
unavailable historical evidence binding rows remain unchanged. The
integration receipt (`artifacts/qual-guards-01/integration.json`) records the
pushed commit, hosted CI results and cleanup.

## Remaining scope

A separate synthetic lifecycle probe (`artifacts/encoder-cleanup-before.log`)
(probe source (`artifacts/probe-encoder-cleanup.py`)) reproduced a still-open
legacy qualifier defect: completed=true despite three false detach receipts and
Host exit code -1 with a supplied forced-stop receipt. Endpoint evidence is stubbed
in this probe because its purpose is lifecycle acceptance, not identity qualification.
No Siemens executable ran. The next repair is to reuse the existing OwnedScratch
controller's exact-handle, shutdown and binary checks rather than retaining the
legacy detach/close bookkeeping. This defect is not repaired by runtime guards.

These changes prevent removable validation; they do not prove every existing
condition is sufficient. Exact process lifetime, compile completeness, source/binary
binding, source-to-recovery-copy endpoint mappings and whole-effect preservation
remain native-handoff audit work. Current endpoint references retain project context;
do not strip it to make cross-copy comparisons pass without independent mapping proof.
The broader script corpus still contains assertions and requires its own scope audits.

Continue TO-03–12 and the [full offline queue](../history/offline-execution-20261016.md).
SDK compilation, genuine fixture execution, recovery/reopen and physical/runtime
acceptance remain unverified. Historical evidence and its unavailable file bindings
are preserved; no native qualification or release readiness is claimed.
