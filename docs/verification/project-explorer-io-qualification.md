# Configured I/O explorer qualification

The positive native run is recorded in `artifacts/project-explorer-native-io-complete.json`, with its portable snapshot in `artifacts/project-explorer-native-io-complete.snapshot.json`. It used the immutable `tia-mcp-v21_20261008_121338_85501a50` candidate; exact host/worker hashes are in the report. This establishes configured address/controller evidence for one local standard CPU/module profile. It does not establish physical topology, terminal/channel wiring, runtime values, or distributed-I/O coverage.

The independently authored fixture uses CPU `OrderNumber:6ES7 511-1AL03-0AB0/V4.1` and the locally discovered `OrderNumber:6ES7 551-1AB01-0AB0/V2.0` module. The runner created an owned offline scratch and used prepared `hardware_plug` to insert the module at the native central rack's slot 2. Native inspection showed a same-named child submodule owning the input range: start byte 0, length 256 **bits** (32 bytes). The qualifier therefore selects the exact address-owning child, not the parent module or a display-name match.

The default scratch table was empty. The runner exported that exact native table, added a single Byte tag `inputByte` through prepared `tag_table_import_xml` at `%IB1`, then used prepared `tag_set` to assign `%IB0`. Both operations compiled and saved successfully. The final native read independently confirmed the exact tag path and address before capture.

Both explorer queries passed: the selected tag's outgoing `configuredIo` relationship and the selected submodule's incoming relationship have the semantic direction **tag → hardware**. Evidence retains the exact hardware source path, native Input range and bit units, `plcOwnershipVerified: true`, `ownershipResolution: verified`, `origin: nativeConfirmed`, and `resolution: resolved`. The evidence's content revision remains explicitly unavailable for the configured-address scope; the metadata hash is never substituted as a content revision.

The positive fixture captured 74 native/display nodes in **519.79 ms**. The separately observed forward relationship analysis took **2996.44 ms**, and the hardware-seeded analysis **268.13 ms**. These are one offline fixture's native measurements, not cached-query/rendering performance or a project-size guarantee. The runner saved the fixture and detached its owned connection normally.

Two earlier reports preserve fixture setup assertions: `project-explorer-native-io.json` found the parent/child display-name ambiguity, and `project-explorer-native-io-positive.json` found the empty default table. Their successful module insertion was retained; no unknown native operation was replayed. The continuation reopened only the exact saved owned project and completed the remaining prepared operations.

The initial candidate used different edge IDs for forward/hardware seed observations of the same binding. Final package `tia-mcp-v21_20261008_125230_8b2f8ce9` passed the read-only recheck in `artifacts/project-explorer-native-io-final.json`: both directions return `io-f26d36ccc8e6cca30b4a`, and the portable export retains exactly one binding. The final 74-node/1-edge snapshot passes Draft 2020-12 validation with zero errors (`artifacts/project-explorer-native-io-final-schema.json`). Its SHA-256 is `6dbdc7ff5204fa2ac40950f20f046b3d2f40c0e3e5dcdeff12dad82833f5651e`. No prepared changes, compilation or save occurred during this recheck; the owned connection detached normally. Final native capture took 852.80 ms, tag analysis 10822.72 ms and hardware analysis 253.00 ms while other native qualification work was active. The verifier reproduces these checks without repeating engineering operations:

```powershell
python scripts/qualify_project_explorer_io.py --verify-report artifacts/project-explorer-native-io-complete.json --report artifacts/project-explorer-native-io-final.json
```

The default release is read from `artifacts/project-explorer-release.txt`. `--verify-report` strictly prohibits prepared changes, refuses a missing/replaced saved tag or differing exact binding, and never retries an unknown outcome. To create a separate new fixture instead, omit `--verify-report`.

The six pure oracle tests in `scripts/test_project_explorer_io.py` pass. They reject unverified overlap, reversed direction, a different same-named hardware path, incorrect range units, and misuse of metadata as content freshness.
