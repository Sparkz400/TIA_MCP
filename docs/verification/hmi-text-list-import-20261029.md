# Native import of authored Unified text lists — 29 October 2026

**Status:** passed for one profile. Evidence:
[`local.hmi.text-list-import.20261029`](../../evidence/local.hmi.text-list-import.20261029.json)
(frozen `artifacts/tl4`, commit `47491ab`, run `artifacts/q/tl4`). Candidates that `hmi_text_list_author`
and `hmi_text_list_generate_set` publish import exactly through V21. Content that V21 reports as imported
but changes is now refused before publication.

## The route exists

The release matrix recorded "no Unified text-list import route" for both tools. That is a gap in this
repository, not in V21. The installed `Siemens.Engineering.WinCCUnified.xml` documents
`HmiSoftware.HmiTextLists` (`HmiTextListComposition`) with `Import(DirectoryInfo, String)`,
`Export(DirectoryInfo, String)` and `Find(String)`, and `HmiTextList.Delete()`. The worker uses only
`Export`. Production HMI writes stay behind the closed preservation gate, so these runs import through the
independent fixture author:

- `HmiSeeder textlist-import <new-output> <candidates>` creates a new owned project with one MTP700 Unified
  Comfort HMI (V21.0.1.0), activates en-US and de-DE and seeds two screens with items, a tag, the start screen,
  a discrete alarm, a screen `Loaded` script (never executed) and the
  [documented bilingual peer list](../../fixtures/hmi/unified-text-list/README.md). It walks the HMI twice,
  compiles, saves and archives a checkpoint. It imports each candidate pair with
  `Import(directory, "<name>.hmi.yml")` and exports all text lists after each import. It then re-imports the
  whole export (no-change roundtrip), walks, compiles, saves and archives. A second, fresh Portal process
  reopens the saved project, retrieves the final archive, retrieves the checkpoint, imports the boundary
  candidates into that restored copy, closes it unsaved and retrieves the checkpoint again. One owned Portal
  runs at a time, and each process must exit.
- [`qualify_hmi_text_list_import.py`](../../scripts/qualify_hmi_text_list_import.py) authors and reopens the
  candidates on the production Host (no native workers), checks the refusals, runs the seeder and compares the
  recorded exports with the candidates. Unit tests for its comparison:
  [`test_qualify_hmi_text_list_import.py`](../../scripts/test_qualify_hmi_text_list_import.py).

## How V21 stores an import

The export is not the authoring format, so the runner compares meaning, not bytes:

- Entries are renamed (`Text_list_entry_0`, …). All texts of the device move into one library,
  `MyTextLibrary`, renumbered `Text_0`, `Text_1`, … on every export. The candidate's library name, entry and
  text IDs and `DefaultLanguage` are not kept.
- Fields at their default are omitted: `RangeType: Decimal`, `Type: SingleValue`, `Value`/`FromValue`/`ToValue`
  0 and `IsDefaultEntry: false`. The container is `DeviceTextList`, files use CRLF, and multi-line texts are
  YAML block scalars.
- Texts such as `Off`, `On` or `Null` are written unquoted, which a YAML 1.1 reader (PyYAML) turns into
  booleans or null, and a tab can sit inside an unquoted text, which PyYAML cannot scan. The runner reads
  library texts as strings and carries tabs through a placeholder.

The runner compares each list as a sorted multiset of entries with those defaults applied and every text
reference replaced by its per-language text. Every stated value stays significant. Because of the renaming,
a native export never equals a candidate byte for byte: a v2/v3 regeneration needs the retained candidate
pair as its current files, not a native export.

## What V21 changes, and what the tools now refuse

Runs `q/tl2` and `q/tl3` imported tool-authored probes. Every import returned `true`; the readback showed:

| Authored content | Native result | Refusal code |
| --- | --- | --- |
| Values outside signed 32-bit (±2^31, int64 extremes) | silently 0 | `HmiTextListValueOutOfRange` |
| Default entry in a `Bool` list | dropped | `HmiTextListBoolDefaultEntry` |
| Default entry that is not `SingleValue` 0 (a `To` −1, a −2^31) | imported as `SingleValue` 0 | `HmiTextListDefaultEntryValue` |
| NUL and most other control characters in a text | the whole text becomes empty | `HmiTextListTextUnsupported` |
| U+000B, U+000C | replaced with `?` | `HmiTextListTextUnsupported` |
| CR LF, NEL (U+0085) | read back as LF | `HmiTextListTextUnsupported` |
| List names with `.`, `:`, `#`, `"`, `[`, `$`, `%`, `*`, `/`, `?`, `]`, `` ` ``, `~`, or 256 characters | list not created or renamed `Text_list_N` | `HmiTextListNameUnsupported` |
| List names with a leading or trailing space | trimmed | `HmiTextListNameUnsupported` |
| Text reference to a missing library entry (runner-made pair) | entry imported with empty texts | `HmiTextListUnresolvedText` |

From commit `47491ab` both tools refuse these classes for new candidates, before anything is published, and
name the list and entry. Texts may contain any character except a control character other than tab and LF.
List names follow an allowlist: letters, digits, inner spaces and `!&'()+,-;<=>@\^_{|}`, at most 128
characters. Records published earlier keep their meaning and still reopen; a managed test reopens a retained
record whose content is now refused. Refused integer states include `UDInt` and `LInt` bounds outside signed
32-bit, so their full domains can no longer be listed.

## Results (run `q/tl4`, 227 checks, no failures)

| Gate | Result |
| --- | --- |
| Refusals over MCP: all six codes, each before publication | passed |
| Ten candidate pairs: v1 retained, v2 Bool initial and regenerated, v3 integer initial and regenerated, both outputs (`RunList`, `StateList`) of an initial and an evolved v1 set, and the v1 base that replaces an identical list | each imports exactly; every other list unchanged; no orphan texts |
| Replacing an existing list (`States` four times, `RunList` and `StateList` once each) | the new content replaces the old exactly |
| No-change roundtrip: whole export imported again | semantically equal, and the re-export is byte-identical |
| Independent walk of tags, tag tables, screens, items, events, scripts, alarms, connections and graphic lists | unchanged by the imports; repeated baseline walk equal |
| Compile, after import and after reopen | complete diagnostics; errors and warnings unchanged (the fixture's 2 encrypted-transfer password errors) |
| Fresh Portal: saved project reopened, final archive retrieved | text lists and walk equal the post-import state |
| Five boundary candidates on the restored checkpoint | all import exactly (below) |
| Pre-import checkpoint retrieved, before and after the boundary candidates | equals the baseline; the archive is unchanged |
| Host: retained author and set records, the three oracle variants and three boundary records on fresh Hosts | exact receipts and bytes; tamper and forged-record refusals |
| Owned processes | both Portals (23052, 37672) exited; both Hosts exited 0 |

The boundary candidates are at the edge of what the tools still accept: `To`, `Range` and `From` entries,
signed 32-bit bounds, `SingleValue` 0 defaults in `Decimal` and `BitNumber` lists, a `Bool` list without a
default; quotes, backslash, tab, LF, umlauts, CJK, an emoji, empty texts, YAML-like syntax, U+2028/U+2029,
soft hyphen, ZWSP, ZWJ, BOM, RLO, WJ and a 16384-character text; list names of 127 and 128 characters, with
Latin, umlaut, CJK, Cyrillic, Greek, Arabic, Hebrew and Hangul letters, Arabic-Indic digits, inner spaces and
every allowed punctuation character. All import exactly.

The same frozen Host also passed the actual-prior runners: v1–v3 authors (188 checks) and v1 sets with v1/v2
evolution (142 checks), both curated into the same record.

## Not covered

- A clean HMI compile: the password errors need native editor work (V21 has no public setter).
- A production import or restore route: no MCP tool imports Unified text lists, and `hmi_engineering` stays
  behind the closed preservation gate.
- Consumers: no screen item uses an imported list; replacing a list that dynamizations reference is untested.
- Text characters other than the tested ones are accepted without native proof; names are allowlisted instead.
- Unified PC and other devices, other languages, runtime and visual behavior.

## Runs not cited

- `artifacts/q/tl1` (`9e130e9`): the first oracle read only the `TextList` container and compared entries by
  key, so every comparison failed; its data showed the export rules above.
- `artifacts/q/tl2` (`52d440a`): passed 211 checks before the refusals existed and found the first five
  content classes; its summary was replaced by this record.
- `artifacts/q/tl3` (`6886526`): found the remaining control characters, name characters, name length and
  default-entry values; its oracle stopped at the unquoted tab.
- `artifacts/q/tl3d`, `artifacts/q/tl4d`: Host-only dry runs of phase 1 with no seeder.
