# Scratch compiler envelope and safety target coverage — 17 October 2026

Tracing the native producer exposed a real mismatch: ScratchCreate returned flat
compiler diagnostics, while the qualification consumers required a targets array.
Synthetic fixtures used the aggregate form and did not reveal the mismatch. The
native producer now retains every existing flat field and adds one named target
with the actual selected PLC path. Both views are independent JSON copies of the
same diagnostic observation; no second compile or diagnostic enumeration occurs.

The linked pure C# SingleTarget helper is used by ScratchCreate and tested without
the Siemens SDK. Its emitted synthetic receipt (`artifacts/compiler-targets-csharp.json`)
also passes the Python reader with the selected PLC and refuses a different PLC.
The before/after reproduction (`artifacts/compiler-targets-reproduction.json`)
records the old shape refusal and old acceptance of a clean unrelated target.

The shared Python validator now rejects duplicate target identities. Callers that
know the required selection provide expected_targets; missing, additional or
different targets refuse. Direct safety scratch creation checks its compiler
target against the unique inventoried PLC before safety inspection. Retained
scratch admission checks the original creation compiler target against its original
inventory and project/process ownership. Safety import, recovery recompilation and
the shared post-apply check require the exact selected PLC. The retained safety
auditor applies the same check to every observed compiler-bearing operation and
reports observedCompileTargetCoverageVerified separately from overall coverage.

This does not establish a fresh baseline compile after opening an existing scratch
or retrieving an archive. The multi-device hardware fixture and encoder hardware
flows still need independent required-device target inventories. Accordingly,
compileTargetCoverageVerified remains false in the safety audit: observed receipts
are checked, but all required compilation phases are not yet proven.

The changed tia_create_scratch route is marked unverified in the feature catalog.
Historical evidence references remain intact; they do not qualify this changed
native output. Source bindings are refreshed without replacing any of the 106
unavailable historical evidence records.

## Verification

Four added Python regression methods cover exact target-set failures, validation
of both flat and target diagnostics, unrelated/duplicate safety PLC results and
wrong baseline target refusal in both fresh execution and retained admission.
Existing safety fixtures now explicitly identify their PLC. The focused run passes
52 parent tests, including the -O/-OO subprocess checks. The linked C# suite passes
11 groups and its emitted JSON is consumed by the actual Python validator.

Frozen Git tree: `7c6cd6c6bc1faf977983b25f10dfb6053a357cad` (2005 files).
Source manifest (`artifacts/compiler-targets-01/source-manifest.json`) SHA-256:
`e9c3ab87e8bf5ca226a64d0fe6d231e5bc0889588fea54637b97a7ec9978dc9d`.
Source ZIP SHA-256: `4cb79ec32dc6ee093848f46cbcb73484953af7d3d2a750f247462ffa24cfea56`.

The aggregate (`artifacts/compiler-targets-01/source/artifacts/checks/result.json`)
passes 40 managed suites, 1,395 Python tests (one unavailable-evidence skip),
24 Explorer model/client checks, contracts/source bindings and all offline MCP
workflows. The general executable passes 202 checks and the linked pure compiler
suite passes 11 groups. The Python optimization runner executes 123 focused cases
under each of -O and -OO; child counts are not added to the parent count.
Run: 2026-10-17T10:26:37.4574091Z to 2026-10-17T10:39:38.5963953Z.
Receipt SHA-256: `78a4d246941bbab830ef8fd5c4004fff8e08553a2020ce40872702507df18150`.
Console evidence (`artifacts/compiler-targets-01/aggregate-console.log`).

All 22 package stages (`artifacts/compiler-targets-01/source/artifacts/package-checks/result.json`)
pass, including relocation. Bundle `tia-mcp-offline_20261017_104017_63edcc56` contains 1421
manifest files. Receipt SHA-256: `e0952a71567b4eb7317910e02758a56ac2646fac305c8af361a27e114ba5df57`.
Manifest SHA-256: `97d04a277d25cdb299c5e9b744431a9a799c2e9aab7e1d36c9238754ead26da5`.
ZIP SHA-256: `8e2cedf02843047279c1f0313d0d65208987b8ca0f0179f756f973ca13b45387`.
The portable package excludes native workers; neither it nor the linked pure
compiler tests compile the full Siemens worker against the SDK. Browser tests
were not repeated; the Explorer model/client tests are not browser acceptance.

The source comparison (`artifacts/compiler-targets-01/final-source-comparison.json`)
allows only completion-documentation changes after testing and verifies all 106
unavailable historical evidence binding rows remain unchanged. The
integration receipt (`artifacts/compiler-targets-01/integration.json`) records
the pushed commit, hosted CI outcome and cleanup.

## Remaining gates

The native worker is not built against the Siemens SDK here. Scratch creation,
native target-path correctness, diagnostic completeness in actual projects and
installed-product compatibility need native qualification. Fresh baseline compile
and complete hardware/compiler target sets remain implementation work. D14
reporting/F-I/O/activation, TO-03–12 and the full offline queue remain open. No
functional-safety or release acceptance is claimed.
