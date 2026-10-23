# Complete device compiler scope - 17 October 2026

Project-wide hardware and non-HMI library compilation previously identified
compiled devices only by display name. Devices in different groups can share a
name, and those receipts did not identify an independently required target set.
The compiler loop could also dispatch earlier devices before discovering that a
later device lacked a compiler service.

The shared native helper now materializes the bounded root, ungrouped and nested
group device inventory before resolving every compiler service. Missing names,
duplicate paths, repeated device references, enumeration failures and bounds
refuse before dispatch. Each successful receipt retains its legacy display name
and adds its exact targetPath, requiredTargets, device-path-v1 policy and complete
scope flag. After compilation, the helper checks that exact paths and object
identities still match. It does not retry an uncertain compiler call. Library
preview now includes compileTargetPaths, and library journal entries identify the
exact device path. Missing library compiler services now use the shared
UnsupportedCapability refusal instead of the prior TargetIncompatible code.

The independent Python reader checks all declared target paths and can compare
them with complete immutable device_inventory pages. It rejects incomplete,
filtered, warning-bearing, inconsistent, duplicated or malformed inventories.
Safety scratch creation and encoder hardware qualification capture those pages
through the existing owned session, with owner guards before and after capture.
The encoder source auditor derives coverage from original retained inventory
calls, not merely the producer's summary. This includes empty devices.

Six linked-production C# test groups exercise exact encoded paths, repeated names,
complete service preflight, enumeration and identity refusal, depth/size bounds,
scope drift and compiler failure without replay. Synthetic Siemens API shapes
exercise the actual helper; they do not establish SDK compatibility. Five added
Python methods cover device receipts, pagination, independent target mismatch,
original source admission and safety hardware refusal. Existing optimization-mode
coverage includes these modules. Malformed inventory-object cases also refuse.

The initial device-compiler-scope-01 snapshot predates a documented native
identity correction and is baseline evidence only. [Siemens V21 object equality](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/verifying-object-equality)
requires Equals for engineering identity; distinct wrappers can represent the
same native object. The added wrapper regression reproduces 207/208 passing with
ReferenceEquals and passes after using Equals. The repeated-device guard also
rejects distinct wrappers sharing one engineering identity. Replacement by a
new identity at the same path still refuses. Before log (`artifacts/device-compile-equality-before.log`).

The cross-language receipt (`artifacts/device-compiler-scope-02/cross-language-compiler.json`) retains actual output from the linked C# helper and the independently specified Python target set. Missing, wrong-project and duplicate expected targets refuse. The helper, test and Python source bytes match the final frozen snapshot.

The frozen aggregate cross-language check (`artifacts/device-compiler-scope-02/cross-language-frozen-compiler.json`) repeats the same independent checks using the actual final C# output and the Python consumer from that frozen source tree.

Frozen Git tree: `915fd7b7f7b30ff3e46ddcc84242653068580551` (2008 files).
Source manifest (`artifacts/device-compiler-scope-02/source-manifest.json`) SHA-256:
`67c602f11298a39814d168a7cdebf0e57c9a0c30d1953e133aa99c9ed1d92fa7`.
Source ZIP SHA-256: `c96d3e6521573222ce11e21c9ffa751ef082804c34ccaa20d5418fafe09d0d93`.

The aggregate (`artifacts/device-compiler-scope-02/source/artifacts/checks/result.json`)
passes 40 managed suites, 1,400 Python tests (one unavailable-evidence skip),
24 Explorer model/client checks, contracts/source bindings and all offline MCP
workflows. The general executable passes 208 checks and the linked pure compiler
suite passes 11 groups. The Python optimization runner executes 128 focused cases
under each of -O and -OO; child counts are not added to the parent count.
Run: 2026-10-17T11:16:20.8462519Z to 2026-10-17T11:31:07.6333207Z.
Receipt SHA-256: `f5ae5a4b23c0b552532c203907691c723e747501c8e466d096a1bd0aaba91bb8`.
Console evidence (`artifacts/device-compiler-scope-02/aggregate-console.log`).

All 22 package stages (`artifacts/device-compiler-scope-02/source/artifacts/package-checks/result.json`)
pass, including relocation. Bundle `tia-mcp-offline_20261017_111952_ad0dc863` contains 1422
manifest files. Receipt SHA-256: `8ea10b7be6039ae01cdc99889c4d883992f6fa044f2f7bcff260346c6124dad6`.
Manifest SHA-256: `82e2456f0e864d3e0cada15046d7793847d543b97d81c7ed3c8421228a75d830`.
ZIP SHA-256: `585a6eb5eaa29f991cfed090832a8865671167f49fa0473cd491b03618969ad8`.
The portable package excludes native workers; neither it nor the linked pure
compiler tests compile the full Siemens worker against the SDK. Browser tests
were not repeated; the Explorer model/client tests are not browser acceptance.

The source comparison (`artifacts/device-compiler-scope-02/final-source-comparison.json`)
allows only completion-documentation changes after testing and verifies all 106
unavailable historical evidence binding rows remain unchanged. The
integration receipt (`artifacts/device-compiler-scope-02/integration.json`) records
the pushed commit, hosted CI outcome and cleanup.

## Remaining qualification

The native worker has not been built against the Siemens SDK or run here. Native
object identity behavior, services, grouping behavior and exact inventory
coverage need product qualification. The postcheck is not a transaction: it cannot
undo native side effects or establish that no concurrent edit occurred and was
reverted. Exceptions can leave partial compiler progress; existing caller recovery
rules remain necessary. Independent library and remote-I/O qualification, seeder
target identity, fresh baseline compilation after opening/retrieving existing
projects, full D14 reporting/F-I/O/activation and TO-03-12 remain open.

All affected hardware/library catalog routes remain unverified. Original evidence
references and all 106 unavailable historical binding rows are preserved. Portable
results do not establish native compatibility, functional safety or release
acceptance. The full 44-workstream goal remains active.
