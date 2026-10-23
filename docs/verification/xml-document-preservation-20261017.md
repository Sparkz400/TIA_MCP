# Whole-document XML preservation — 17 October 2026

The shared native preservation comparator visited only the root element. It
silently discarded comments and processing instructions before and after that
root. The graphical continuation harness also used an older structural
comparator that omitted annotations. The reproduction (`artifacts/xml-document-reproduction.json`)
compares both implementations at `176f1fc` with their repaired versions, using
synthetic XML without starting Siemens software.

The shared comparator now visits all document children in order. Comment text,
processing-instruction target/data, position and multiplicity participate in the
comparison. Existing root/descendant rules remain: IDs, meaningful whitespace,
namespace bindings and unknown payload are retained; the direct unqualified
`DocumentInfo` provenance element and serializer indentation remain excluded.
The XML declaration remains outside this semantic comparison. This is not a
byte-for-byte archive or export comparison.

The policy label is now `native-payload.v3-document-comments-pi-qnames` in the
owned snapshot, authored-route capture and typed graphical/resolution reports.
The shared constant prevents these producers from advertising different policy
versions. Graphical continuation uses the same comparator and records its policy;
its existing snapshot, ownership and graph guards now remain active under Python
optimization. Its original raw XML strings are rehashed without changing the
source report. New canonical hashes differ from v2, including root-only documents.
Historical files, report hashes and unavailable evidence bindings are not migrated
or rewritten. A legacy recorded digest alone does not prove the stronger policy.

## Verification

Four added regression methods cover annotation content/order/count/location,
formatting/declaration equivalence, exact encoder edit/recovery/both saved copies,
and graphical continuation recomputation from original XML. Changed exports carry
valid new byte hashes, so refusal demonstrates payload checking rather than a
simple artifact-hash mismatch. Tests check that audit inputs remain unchanged.
The pre-fix run (`artifacts/xml-document-before.log`) has 14 failed assertions;
the targeted repaired run (`artifacts/xml-document-after-02.log`) passes all 26
parent tests, including the subprocess checks under both `-O` and `-OO`.
Those optimized runs each cover 78 focused cases; child cases are not added to
the parent count.

Frozen Git tree: `acdf127076175a24cde9082739995e6422a147e7` (1991 files).
Source manifest (`artifacts/xml-document-01/source-manifest.json`) SHA-256:
`f82e817090cf6004de764132290364029eaeb7ac25c44da4c15b4c71585eea40`.
Source ZIP SHA-256: `df45f46d4193770d7b37912ab30bec4811f994e00fec8acf2610c656784d1c84`.

The Python aggregate (`artifacts/xml-document-01/source/artifacts/checks/result.json`)
passes all 1,357 tests with one unavailable-historical-evidence skip, plus contracts,
source bindings and retained-evidence inventory. Both optimized child runs pass.
Run: 2026-10-17T09:07:11.7612733Z to 2026-10-17T09:08:29.9439640Z.
Receipt SHA-256: `20b8df40d9be760fb303e624ba1f32a7b642c41435ea0f022b4b7ff26e6d6a05`.
Console evidence (`artifacts/xml-document-01/python-console.log`).

All 22 package stages (`artifacts/xml-document-01/source/artifacts/package-checks/result.json`)
pass, including relocation. Bundle `tia-mcp-offline_20261017_090733_21248f88` contains 1407 files.
Receipt SHA-256: `66c2c9467880e0dae0a36b37f07a70d18c1aeaa0143d2ca5a11ad5442c5fbc52`.
Manifest SHA-256: `5fc8d2538381da833af8b8266175299915e516499c096e1cffa49b2eedf5f936`.
ZIP SHA-256: `1bda1ef1de61da8e76e9be1b916bb41d423937b4ed589b32651c2d2f5b3c00d9`.
The offline package excludes native workers. These hashes establish byte integrity,
not publisher identity or native authenticity.

The source comparison (`artifacts/xml-document-01/final-source-comparison.json`)
checks that only completion documentation changed after the frozen run and all 106
unavailable historical evidence rows are preserved. The
integration receipt (`artifacts/xml-document-01/integration.json`) records the
pushed commit, hosted CI outcome and cleanup.

The prior full managed/Explorer/general/MCP aggregate at `176f1fc` remains its
own evidence. This increment changes Python qualification/comparison code only;
managed binaries and browser code are unchanged. No managed-suite or browser
rerun is claimed for this increment.

## Remaining gates

No Siemens SDK build, native archive/export/retrieval, PLC or simulator execution
was possible. The stronger comparator detects observed collateral changes; it
does not establish complete capture, cross-copy endpoint identity, compile-target
coverage or whole-project native preservation. Other harness lifecycle and
evidence-admission obligations and the full release queue remain open.
