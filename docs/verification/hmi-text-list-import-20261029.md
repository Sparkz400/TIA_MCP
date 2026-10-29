# Native import of authored Unified text lists — 29 October 2026

**Status:** passed for one profile. Evidence:
[`local.hmi.text-list-import.20261029`](../../evidence/local.hmi.text-list-import.20261029.json)
(frozen `artifacts/tl2`, commit `52d440a`, run `artifacts/q/tl2`). Candidates that `hmi_text_list_author`
and `hmi_text_list_generate_set` produce import exactly through V21 within the bounds below. V21 silently
alters content outside them, and the tools do not refuse it yet.

## The route exists

The release matrix recorded "no Unified text-list import route" for both tools. That is a gap in this
repository, not in V21. The installed `Siemens.Engineering.WinCCUnified.xml` documents
`HmiSoftware.HmiTextLists` (`HmiTextListComposition`) with `Import(DirectoryInfo, String)`,
`Export(DirectoryInfo, String)` and `Find(String)`, and `HmiTextList.Delete()`. The worker uses only
`Export`. Production HMI writes stay behind the closed preservation gate, so this run imports through the
independent fixture author:

- `HmiSeeder textlist-import <new-output> <candidates>` creates a new owned project with one MTP700 Unified
  Comfort HMI (V21.0.1.0), activates en-US and de-DE and seeds two screens with items, a tag, the start screen,
  a discrete alarm, a screen `Loaded` script (never executed) and the
  [documented bilingual peer list](../../fixtures/hmi/unified-text-list/README.md). It walks the HMI twice,
  compiles, saves and archives a checkpoint. It imports each candidate pair with
  `Import(directory, "<name>.hmi.yml")` and exports all text lists after each import. It then re-imports the
  whole export (no-change roundtrip), walks, compiles, saves and archives. A second, fresh Portal process
  reopens the saved project, retrieves the final archive, retrieves the checkpoint, runs boundary probes on
  that restored copy, closes it unsaved and retrieves the checkpoint again. One owned Portal runs at a time,
  and each process must exit.
- [`qualify_hmi_text_list_import.py`](../../scripts/qualify_hmi_text_list_import.py) authors and reopens the
  candidates on the production Host (no native workers), runs the seeder and compares the recorded exports
  with the candidates. Unit tests for its comparison:
  [`test_qualify_hmi_text_list_import.py`](../../scripts/test_qualify_hmi_text_list_import.py).

## How V21 stores an import

The export is not the authoring format, so the runner compares meaning, not bytes:

- Entries are renamed (`Text_list_entry_0`, …). All texts of the device move into one library,
  `MyTextLibrary`, renumbered `Text_0`, `Text_1`, … on every export. The candidate's library name, entry and
  text IDs and `DefaultLanguage` are not kept.
- Fields at their default are omitted: `RangeType: Decimal`, `Type: SingleValue`, `Value`/`FromValue`/`ToValue`
  0 and `IsDefaultEntry: false`. The container is `DeviceTextList`, files use CRLF, and multi-line texts are
  YAML block scalars.
- Texts such as `Off`, `On` or `Null` are written unquoted. A YAML 1.1 reader (PyYAML) turns them into
  booleans or null; the runner reads library texts as strings.

The runner compares each list as a sorted multiset of entries with those defaults applied and every text
reference replaced by its per-language text. Every stated value stays significant. Because of the renaming,
a native export never equals a candidate byte for byte: a v2/v3 regeneration needs the retained candidate
pair as its current files, not a native export.

## Results (run `q/tl2`, 211 checks, no failures)

| Gate | Result |
| --- | --- |
| Ten candidate pairs: v1 retained, v2 Bool initial and regenerated, v3 integer initial and regenerated, both outputs (`RunList`, `StateList`) of an initial and an evolved v1 set, and the v1 base that replaces an identical list | each imports exactly; every other list unchanged; no orphan texts |
| Replacing an existing list (`States` four times, `RunList` and `StateList` once each) | the new content replaces the old exactly |
| No-change roundtrip: whole export imported again | semantically equal, and the re-export is byte-identical |
| Independent walk of tags, tag tables, screens, items, events, scripts, alarms, connections and graphic lists | unchanged by the imports; repeated baseline walk equal |
| Compile, after import and after reopen | complete diagnostics; errors and warnings unchanged (the fixture's 2 encrypted-transfer password errors) |
| Fresh Portal: saved project reopened, final archive retrieved | text lists and walk equal the post-import state |
| Pre-import checkpoint retrieved, before and after the probes | equals the baseline; the archive is unchanged |
| Host: retained author and set records, the three oracle variants and three boundary records on fresh Hosts | exact receipts and bytes; tamper and forged-record refusals |
| Owned processes | both Portals (26276, 41004) exited; both Hosts exited 0 |

The same frozen Host also passed the actual-prior runners: v1–v3 authors (188 checks) and v1 sets with v1/v2
evolution (142 checks), both curated into the same record.

## Boundaries: V21 accepts and alters

Every probe import returned `true`. The readback shows what V21 kept:

| Authored content | Native result |
| --- | --- |
| `To`, `Range`, `From` entries; signed 32-bit bounds | exact |
| Values outside signed 32-bit (±2^31 and the int64 extremes) | silently 0 |
| Default entry in a `Bool` list | dropped; the default entry of a `BitNumber` list is kept |
| Quotes, backslash, tab, umlauts, CJK, an astral emoji, empty texts, YAML-like syntax, U+2028/U+2029 | exact |
| CR LF and NEL (U+0085) | read back as LF; the export writes texts as YAML block scalars, so CR is not observable through it |
| NUL in a text | the whole text becomes empty |
| List names with space, umlaut, CJK or dash | exact |
| List names with `.`, `:`, `#`, `"` or `[` | list not created |
| Text reference to a missing library entry (runner-made pair) | entry created with empty texts |

`hmi_text_list_author` accepts all of these (int64 bounds, any list name up to 256 characters, any text), so
the tool can publish candidates that V21 changes without an error. Its native qualification therefore holds
only inside these bounds.

## Not covered

- A clean HMI compile: the password errors need native editor work (V21 has no public setter).
- A production import or restore route: no MCP tool imports Unified text lists, and `hmi_engineering` stays
  behind the closed preservation gate.
- Consumers: no screen item uses an imported list; replacing a list that dynamizations reference is untested.
- Unified PC and other devices, other languages, runtime and visual behavior.

## Development run not cited

`artifacts/q/tl1` (commit `9e130e9`) ran the same native sequence. Its oracle read only the `TextList`
container and compared entries by key, so every comparison failed. The native data showed the export rules
above, and the corrected oracle (`52d440a`) passes on that run's data as well. `tl2` adds the boundary
requests.
