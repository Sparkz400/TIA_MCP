# Real vendor library workload — 8 November 2026

| Evidence | Runner | Result |
| --- | --- | --- |
| [`local.library.vendor-workload.20261108`](../../evidence/local.library.vendor-workload.20261108.json) | `qualify_vendor_workload.py` | 18 of 22 archives upgraded, 4 refused typed |

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

Not covered: instantiating or compiling the upgraded types; PAL's V16 SimaticML, which still needs an intermediate TIA
version; global-library branching graphs at this scale.
