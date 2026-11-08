# Real vendor library workload — 8 November 2026

| Evidence | Runner | Result |
| --- | --- | --- |
| [`local.library.vendor-workload.20261108`](../../evidence/local.library.vendor-workload.20261108.json) | `qualify_vendor_workload.py` | 18 of 22 archives upgraded, 4 refused typed |
| [`local.library.vendor-workload-all.20261108`](../../evidence/local.library.vendor-workload-all.20261108.json) | the same, under the Host's default root | 22 of 22 upgraded |
| [`local.library.vendor-compile.20261108`](../../evidence/local.library.vendor-compile.20261108.json) | `qualify_vendor_workload.py --compile` | 180 default blocks compiled |

The [scale record](library-native-scale-20261027.md) left real vendor byte volumes open: its 150 MB package was generated
in the shape of DMC Open Library V17. This run uses the seven real downloads named in the
[intake review](vendor-intake-review-20261025.md), each checked against its hash first, in one owned TIA Portal V21.

| Step | Result |
| --- | --- |
| Intake of all seven packages (467,587,713 bytes) | 6.0 s; 55 PAL XML sources, 22 DMC library archives, 1 example project archive |
| `library_upgrade` of every DMC archive, V14 SP1 to V19 | 18 upgraded: 6,346 types, 6,364 versions, 31–76 s each, 871 s together |
| Portal working set | 1,251 MiB at start, 4,590 MiB after the last upgrade (one Portal for all) |
| V17, V18 and V19 Professional, V14 SP1 (S7-300) | refused at prepare with `LibraryPathTooLong`, nothing copied |

## The path limit it found

A first run under the same 52-character artifact root upgraded only 4 of 22: TIA refused the others with
`NativeLibraryError`, "The specified path ... is too long". `RetrieveWithUpgrade` retrieves into `<work>\<name>`, opens
`<work>\<name>_V21`, and refuses a path over 143 characters. The upgrade folders are now one letter each
(`change-lib-<id>\u\w`), and prepare measures `<work>\<name>_V21` and refuses with `LibraryPathTooLong` before any copy.
A change prepared with the earlier `upgraded\source`/`upgraded\work` layout still applies. The four remaining names are
one character over under this root; a shorter artifact root upgrades them.

## Under the default root

The Host's default artifact root here is the checkout's `artifacts` folder (39 characters). A rerun with the runner's
directory directly below it upgraded **all 22 archives**: 7,726 types and 7,802 versions in 1,116 s, the four longer
names included (39–57 s each); the Portal working set grew from 1,156 MiB to 4,841 MiB.

## Instantiated and compiled

`--compile 'Open Library V19 PLC.zal19'` upgraded only that archive with an owned scratch CPU 1511-1 PN V4.1 open, set
DMC's system and clock memory bytes, created its "Open Library" tag table, and instantiated **180 of the library's 218
default code-block versions** with their dependency closures in two `library_instantiate` changes (100 in 60 s, 80 in
48 s), each with checkpoint, a compile that had to pass, and save. The 38 excluded by name are the Modbus family
(S7-1200 instructions on this S7-1500), SIWAREX (know-how protection) and `fbVFD_V20` (a missing dependency), as the
2 November probe diagnosed. The Serialize conflict that probe saw did not occur in this order.

Not covered: the 38 excluded types; download or runtime behavior of the vendor blocks; PAL's V16 SimaticML, which still
needs an intermediate TIA version; global-library branching graphs at this scale.
