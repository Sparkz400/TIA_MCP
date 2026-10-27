# Typed wave 9 families: capture and qualification runbook

Prepared 27 October 2026. **Not executed.** Nothing on this page is evidence until
the steps below run on the engineering PC and their reports are curated.

Commit 81170e3 taught the typed builder DInt/Real comparisons, MOVE,
ADD/SUB/MUL/DIV, TONR and R_TRIG/F_TRIG. Every request for them still returns
`GraphicalProfileUnavailable`, because `fixtures/typed-graphical/profiles.v1.json`
has no profile for them. This runbook covers four steps for the standard route,
on CPU 1511-1 PN `OrderNumber:6ES7 511-1AL03-0AB0/V4.1` with TIA Portal V21 Update 2:

1. Capture independently handwritten references for these families.
2. Stage the captures as profile candidates and audit them.
3. Promote the audited profiles.
4. Qualify the builder against them.

## What is prepared

| Family | Types | Reference blocks (LAD and FBD) | Profile id | Case ids |
| --- | --- | --- | --- | --- |
| eq/ne/lt/le/gt/ge | DInt, Real | `Tgp_<Op><Type>_<LANG>` | `v21.fw41.<lang>.compare.<op>.<type>.v1` | `typed-<op>-<type>-<lang>` |
| MOVE | Int, DInt, Real | `Tgp_Move<Type>_<LANG>` | `v21.fw41.<lang>.move.<type>.v1` | `typed-move-<type>-<lang>` |
| ADD/SUB/MUL/DIV | Int, DInt, Real | `Tgp_<Add\|Sub\|Mul\|Div><Type>_<LANG>` | `v21.fw41.<lang>.math.<OP>.<type>.v1` | `typed-<op>-<type>-<lang>` |
| TONR | Time | `Tgp_TONR_<LANG>`, `Tgp_TONR_Global_<LANG>`; DB `Tgp_GlobalTonr` (`IEC_TIMER`) | `v21.fw41.<lang>.timer.TONR.time.v1` | `typed-tonr-<lang>`, `typed-existingglobal-tonr-<lang>` |
| R_TRIG/F_TRIG | none | `Tgp_R_TRIG_<LANG>`, `Tgp_R_TRIG_Global_<LANG>` (F_TRIG alike); DBs `Tgp_GlobalRTrig`, `Tgp_GlobalFTrig` | `v21.fw41.<lang>.edge.R_TRIG.v1` | `typed-rtrig-<lang>`, `typed-existingglobal-rtrig-<lang>` (ftrig alike) |

In total: 69 sources (3 global storage DBs and 66 callers), 60 profiles (6 of them
with global storage) and 66 cases. Of the cases, 60 have runtime suites; the 6
`existingglobal` cases are engineering only, like the TON/CTU existing-global cases.

- `fixtures/typed-graphical/author_wave9_families.py` writes the sources, the packet
  and the cases. Rerun it after any edit; it never touches other fixtures.
- `fixtures/typed-graphical/wave9-capture-packet.v1.json` holds the source hashes, the
  SHA-bound baseline archive, the exact probe and capture commands and
  `status: authored-not-executed`.
- `fixtures/typed-graphical/wave9-cases.v1.json` holds the typed requests;
  `fixtures/typed-graphical/behavior.py` holds their runtime expectations.
- `scripts/extract_typed_graphical_profiles.py` holds the reviewed pin contracts. It
  stops if a wired native pin is missing from a contract, or a required contract pin
  is not wired.

## Unverified assumptions

The repository had no earlier SIMATIC SD for MOVE, arithmetic or edge boxes, so the
sources assume:

1. MOVE and arithmetic boxes take their cardinality from the pins used, as `A`/`O`
   do. ADD/SUB/MUL/DIV take `{ S7_Templates := "SrcType := <type>" }`. Native Part
   names are `Move`, `Add`, `Sub`, `Mul` and `Div`; the extractor accepts any casing.
2. MOVE, arithmetic, R_TRIG and F_TRIG boxes have EN/ENO in LAD and FBD. LAD feeds
   the power rail into `en`; FBD wires `TRUE` to it (`RUNG TRUE`, as the captured
   FC/FB calls do). The pins are MOVE `en in out1 eno`, math `en in1 in2 out eno`
   and edge `en CLK Q eno`.
3. R_TRIG/F_TRIG use the IEC instance syntax `#Storage.R_TRIG( clk := …, q => … )`,
   and multi-instance type `R_TRIG`/`F_TRIG`. Their global instance DBs come from the
   specs `global-rtrig-storage.instancedb.json`/`global-ftrig-storage.instancedb.json`
   through `instance_db_create` (`CreateInstanceDB`), each admitted after the first
   multi-instance use so TIA has copied the system FB. SCL `DATA_BLOCK`s of `"R_TRIG"`
   vanished after compile and save on 27 October.
4. TONR has pins `IN R PT Q ET`, multi-instance type `TONR_TIME` and an `IEC_TIMER`
   global DB.

Step 1 tests assumptions 1 to 3 natively, and step 3 rejects a pin contract that
differs from the wired native pins. If TIA rejects a source:

1. Draw that one box in a throwaway scratch FB.
2. Read it with `block_read` `{"blockName": "<FB>", "format": "simaticSd"}` to see
   the syntax TIA accepts.
3. Correct the text in `author_wave9_families.py` by hand; the references stay
   handwritten.
4. Rerun step 0 and repeat step 1 into a new output directory.

## Preconditions

- TIA Portal V21 Update 2 and PLCSIM Advanced V8. No other session may use TIA: every
  runner starts and owns its own TIA and PLCSIM processes.
- Build the Debug binaries with `.\scripts\Build.ps1`. The paths below are the
  Debug defaults.
- The baseline archive below must exist with its hash. It is the software-unit
  baseline that the standard IEC effect capture `local.typed.iec-effect-standard.20261026`
  also used with `--unit ''`.
- Every output directory must be new. Keep paths short: TIA limits project paths to
  143 characters.

```powershell
$hostExe = 'src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe'
$worker = 'src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe'
$sim = 'src/TiaMcp.Simulator.V8/bin/Debug/net48/TiaMcp.Simulator.V8.exe'
$cpu = 'OrderNumber:6ES7 511-1AL03-0AB0/V4.1'
$archive = 'artifacts/changes/change-12d1d3b9e3a74cd3be6f53b9f2f35cf4/before.zap21'
$archiveSha = '39cea56a73b8e58263e256c2f27b2402e1d29989fb12cb471f66970e1cce74d6'
```

## Step 0: offline check

```powershell
python -B fixtures/typed-graphical/author_wave9_families.py
git status --short fixtures/typed-graphical
python -B scripts/test_typed_profile_staging.py
python -B scripts/test_typed_graphical_qualification.py
python -B scripts/test_typed_graphical_contracts.py
(Get-FileHash $archive -Algorithm SHA256).Hash.ToLower() -eq $archiveSha
New-Item -ItemType Directory -Force artifacts | Out-Null
python -B -c "import json; p=json.load(open('fixtures/typed-graphical/wave9-capture-packet.v1.json')); open('artifacts/w9c.args','w').write('\n'.join(p['argsFile'])+'\n')"
```

Expected results:

- The author script prints `69 wave 9 references and 66 cases authored; packet status authored-not-executed.`
- `git status` prints nothing.
- All three test files print `OK`.
- The hash check prints `True`.
- `artifacts/w9c.args` lists the 69 sources, each storage DB before its global callers.

## Step 1: syntax probe (diagnostic, not evidence)

The probe imports every source into one fresh scratch and continues past rejections.

```powershell
$sources = Get-Content artifacts/w9c.args
python -B scripts/capture_typed_graphical_profiles.py @sources --host $hostExe --worker $worker --output artifacts/w9p --cpu-type-identifier $cpu
python -B -c "import json,pathlib; r=json.load(open('artifacts/w9p/capture.json',encoding='utf-8')); print(r['completed'], sum(f['engineeringVerified'] for f in r['fixtures']), [(pathlib.Path(f['sourcePath']).name, f['failure']['message'][:300]) for f in r['fixtures'] if 'failure' in f])"
```

Expected: `PREPARE` and `CAPTURED` for each of the 69 sources, exit 0, and `True 69 []`.

A `REJECTED` line keeps TIA's diagnostic and the checkpoint recovery in `capture.json`
(`fixtures[].failure`, `recovery`). Correct the source as described under
[Unverified assumptions](#unverified-assumptions), then probe again into
`artifacts/w9p2`, and so on. Probe captures are never extracted.

## Step 2: owned evidence capture

The capture retrieves the SHA-bound baseline into a new owned project and admits each
source once. After each source it checks that exactly the declared block was added
and every other native XML is unchanged. At the end it verifies a fresh-process reopen.

```powershell
python -B scripts/capture_typed_routes_owned.py '@artifacts/w9c.args' --host $hostExe --worker $worker --output artifacts/w9c --archive $archive --sha256 $archiveSha --unit ''
python -B -c "import json; r=json.load(open('artifacts/w9c/capture.json',encoding='utf-8')); print(r['status'], r.get('persistenceVerified'), sum(f['engineeringVerified'] for f in r['fixtures']), r['remainingSources'])"
```

Quote `'@artifacts/w9c.args'`: an unquoted `@` is PowerShell splatting.

Expected: one `CAPTURED <file>` line per source, exit 0, then `passed True 69 []`.

The first failure stops admission: exit 2, `status: "blocked"`, with `failure` and,
for a tracked change, `failureRecovery`. Keep that report, fix the source and capture
again into a new directory such as `artifacts/w9c2`. Use the new directory in steps 3
and 4.

### Edge global storage only

The 27 October run captured everything except the R_TRIG/F_TRIG global storage. This
capture adds it. It re-admits the four multi-instance edge references first: TIA then
copies the R_TRIG/F_TRIG system FBs into the project before `CreateInstanceDB`, and the
extractor needs them to attach `globalInstanceType` to the edge profiles.

```powershell
$edge = 'rtrig-lad.s7dcl', 'global-rtrig-storage.instancedb.json', 'rtrig-global-lad.s7dcl', 'ftrig-lad.s7dcl', 'global-ftrig-storage.instancedb.json', 'ftrig-global-lad.s7dcl', 'rtrig-fbd.s7dcl', 'rtrig-global-fbd.s7dcl', 'ftrig-fbd.s7dcl', 'ftrig-global-fbd.s7dcl' | ForEach-Object { "fixtures/typed-graphical/sources/$_" }
python -B scripts/capture_typed_routes_owned.py @edge --host $hostExe --worker $worker --output artifacts/w9e --archive $archive --sha256 $archiveSha --unit ''
python -B scripts/extract_typed_graphical_profiles.py artifacts/w9e/capture.json --stage-output artifacts/w9es
python -B scripts/audit_typed_profile_candidates.py --catalog artifacts/w9es/profiles.candidate.v1.json --capture artifacts/w9e/capture.json --prerequisite fixtures/typed-graphical/native/Tgp_R_TRIG_LAD.xml --prerequisite fixtures/typed-graphical/native/Tgp_F_TRIG_LAD.xml --prerequisite fixtures/typed-graphical/native/Tgp_R_TRIG_FBD.xml --prerequisite fixtures/typed-graphical/native/Tgp_F_TRIG_FBD.xml --output artifacts/w9es/audit.json
```

Expected: 10 `CAPTURED` lines and exit 0, then `Staged 4 exact native
profiles`, each with `globalInstanceType` `R_TRIG` or `F_TRIG`. The four edge profile ids
are already deployed without global storage, so promotion replaces them instead of
appending, and keeps the committed multi-instance exports.

## Step 3: stage candidates

```powershell
python -B scripts/extract_typed_graphical_profiles.py artifacts/w9c/capture.json --stage-output artifacts/w9s
```

Expected: `Staged 60 exact native profiles: …\artifacts\w9s\profiles.candidate.v1.json`
and 69 native copies in `artifacts/w9s/native/`. The deployed fixtures stay unchanged.
`--stage-output` never reuses a directory, so stage a retry into a new one. The
possible errors:

- `Reviewed pin contract differs from the pins wired in <block>: <pins>`: the native
  box differs from assumption 2 or 4. Compare the export with its source, then
  correct the `pin(...)` contract in the extractor. If a pin name changed, also correct
  the cases in `author_wave9_families.py`. Rerun step 0.
- `Native reference lacks exact instruction: <block>/<name>`: the native Part name
  differs from the expected one beyond casing. Correct the name in the extractor.
- `Global storage reference is not one <type> instance: <db>`: the global DB has an
  unexpected `InstanceOfName`.

## Step 4: audit

```powershell
python -B scripts/audit_typed_profile_candidates.py --catalog artifacts/w9s/profiles.candidate.v1.json --capture artifacts/w9c/capture.json --output artifacts/w9s/audit.json
```

Expected: `{"passed": true, "findings": [], "sources": 69, "profiles": 60, "templates": 0}`
and exit 0. The command takes no `--prerequisite`, because wave 9 re-exports no
committed reference: its global storage DBs are new. Any finding blocks promotion.

## Step 5: promote

Run this only after step 4 has passed. It follows commits 626080e and 4f38d48: it
copies the 69 staged exports into `fixtures/typed-graphical/native/` and refuses to
overwrite an existing file. It then rebinds the paths and appends the candidates to
the deployed catalog. Every qualification flag stays false, as on every deployed
profile; the qualification ledgers below carry the gates.

```powershell
@'
import json, pathlib, shutil
stage, deploy = pathlib.Path("artifacts/w9s"), pathlib.Path("fixtures/typed-graphical")
staged, deployed = stage.as_posix() + "/native/", deploy.as_posix() + "/native/"
candidate = json.loads((stage / "profiles.candidate.v1.json").read_text(encoding="utf-8"))
catalog = json.loads((deploy / "profiles.v1.json").read_text(encoding="utf-8"))
assert not {p["id"] for p in catalog["profiles"]} & {p["id"] for p in candidate["profiles"]}
for source in candidate["sources"]:
    target = pathlib.Path(source["path"].replace(staged, deployed))
    assert source["path"].startswith(staged) and not target.exists(), target
    shutil.copyfile(source["path"], target)
    source["path"] = target.as_posix()
for profile in candidate["profiles"]:
    profile["provenance"] = profile["provenance"].replace(staged, deployed)
catalog["sources"] += candidate["sources"]
catalog["profiles"] += candidate["profiles"]
(deploy / "profiles.v1.json").write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(len(candidate["sources"]), len(candidate["profiles"]))
'@ | python -B -
Copy-Item artifacts/w9s/audit.json fixtures/typed-graphical/wave9-candidates-<YYYYMMDD>.audit.json
python -B scripts/test_typed_graphical_contracts.py
python -B scripts/test_contracts.py
```

Expected: the script prints `69 60` and both checks pass. The catalog gains 60
profiles and 69 sources.

The Debug worker reads `fixtures/typed-graphical/profiles.v1.json` from the repository
root, so no rebuild is needed. Commit the native files, the catalog and the audit
together.

## Step 6: engineering qualification (four gates)

```powershell
python -B scripts/qualify_typed_graphical.py --cases fixtures/typed-graphical/wave9-cases.v1.json --host $hostExe --worker $worker --engineering-only --output artifacts/w9q --artifacts artifacts/w9n
```

Each case creates a fresh scratch for its `cpuTypeIdentifier` and imports its
promoted reference. It then applies the typed request and compares the native
topology and the complete XML, then checks save and reopen and checkpoint recovery.

Expected: exit 0, and `artifacts/w9q/batch.json` reports `status: "passed"` with 66
`completed`. Each `artifacts/w9q/<fixtureId>/result.json` reports
`status: "passed"`, with engineering, preservation, persistence and recovery verified
and runtime not verified.

The batch stops at the first failure (`blockedFixtureId`). A topology mismatch means
the case in `author_wave9_families.py` does not mirror the captured reference: fix the
case, never the reference. To continue, select the remaining ids with `--case`, into
a new output directory. `--stop-file` ends the batch after the active case.

## Step 7: runtime (fifth gate)

`--live` admits exactly one case per run. The 6 `existingglobal` cases stay
engineering only.

```powershell
$runtime = python -B -c "import json; print('\n'.join(c['fixtureId'] for c in json.load(open('fixtures/typed-graphical/wave9-cases.v1.json'))['cases'] if not c.get('engineeringOnly')))"
foreach ($id in $runtime) {
  python -B scripts/qualify_typed_graphical.py --cases fixtures/typed-graphical/wave9-cases.v1.json --case $id --live --host $hostExe --worker $worker --sim-worker $sim --provision-runtime-credential --trust-owned-empty-cpu-certificate --output "artifacts/w9r/$id" --artifacts artifacts/w9n
  if ($LASTEXITCODE -ne 0) { break }
}
```

Expected: 60 runs, each exiting 0 with all five gates in its `result.json`. The
independent expectations in `behavior.py` are:

| Family | Assertions |
| --- | --- |
| DInt compare | All 25 pairs of −2147483648, −1, 0, 1 and 2147483647 |
| Real compare | All 25 pairs of −1.5, 0.0, 0.25, 1.5 and 16777216.0, all exact in 32 bits |
| MOVE | Copies type limits and exact Real values |
| ADD/SUB/MUL/DIV | Exact results inside each type's range, including results at the limits |
| TONR | Two segments of 2 s and 1.5 s reach PT 3 s only together. A 2 s pause is not counted. Q and ET = PT hold after IN falls, and R clears both. |
| R_TRIG/F_TRIG | One pulse of exactly one call per edge and none while the input is held. The edge memory's initial state is never relied on. |

Arithmetic overflow, division by zero and inexact integer quotients are deliberately
not asserted. The repository documents no TIA OUT/ENO semantics for them, so they
remain unverified. Timing is host application-call timing, not PLC scan timing.

## Step 8: record evidence

Replace `<YYYYMMDD>` with the run date. Every command needs a scope and at least one
limitation; describe what actually ran.

```powershell
$binaries = @('--binary', 'src/TiaMcp.Host/bin/Debug/net10.0-windows', '--binary', 'src/TiaMcp.Openness.V21/bin/Debug/net48')
python -B scripts/curate_evidence.py --id local.typed.wave9-capture.<YYYYMMDD> --report artifacts/w9c/capture.json @binaries --scope "<what the capture covered>" --limitation "Native reference capture only; builder qualification is separate."
python -B scripts/summarize_typed_graphical_qualification.py --run artifacts/w9q --output fixtures/typed-graphical/qualification-wave9-<YYYYMMDD>.v1.json --markdown docs/verification/typed-lad-fbd-wave9-<YYYYMMDD>.md --curation-report artifacts/w9q/curation-wave9.json --curation-gates four
python -B scripts/curate_evidence.py --id local.typed.wave9.engineering.<YYYYMMDD> --report artifacts/w9q/curation-wave9.json @binaries --scope "<cells and gates>" --limitation "Engineering only; runtime is recorded separately."
python -B scripts/summarize_typed_graphical_qualification.py --run artifacts/w9r --output fixtures/typed-graphical/qualification-wave9-runtime-<YYYYMMDD>.v1.json --markdown docs/verification/typed-lad-fbd-wave9-runtime-<YYYYMMDD>.md --curation-report artifacts/w9r/curation-wave9-runtime.json
python -B scripts/curate_evidence.py --id local.typed.wave9.runtime.<YYYYMMDD> --report artifacts/w9r/curation-wave9-runtime.json @binaries --binary src/TiaMcp.Simulator.V8/bin/Debug/net48 --scope "<cells and gates>" --limitation "Host application-call timing; not exact PLC scan timing."
```

| Evidence | Covers | Source |
| --- | --- | --- |
| `local.typed.wave9-capture.<YYYYMMDD>` | 69 native references, persistence, unrelated XML preserved | `artifacts/w9c/capture.json` |
| `fixtures/typed-graphical/wave9-candidates-<YYYYMMDD>.audit.json` | Binding audit of the 60 promoted profiles | Step 4 |
| `local.typed.wave9.engineering.<YYYYMMDD>` | 66 cells, four gates | `artifacts/w9q` |
| `local.typed.wave9.runtime.<YYYYMMDD>` | 60 cells, five gates | `artifacts/w9r` |

Then update the records:

- Add each evidence id to the catalog's evidence list, shaped like
  `local.typed.standard-created-storage-runtime.20261026`.
- Add it to `qualification.evidenceIds` of `change.graphical_typed_edit`.
- Narrow the 27 October limitation of that feature, the wave 9 paragraph in
  `docs/guides/typed-lad-fbd-builders.md` and the capture item in
  `docs/plans/typed-lad-fbd-todo.md` to the cells that actually passed.

## Known limits after a full pass

- The standard route on CPU 1511 V4.1 only. There are no software-unit, namespace or
  second-CPU profiles.
- No created storage for TONR, R_TRIG or F_TRIG (`createMulti`/`createGlobal`): that
  needs captured IEC dependent-instance effect members for `TONR_TIME`, `R_TRIG` and
  `F_TRIG`, which `author_iec_effect_sources.py` does not author.
- The three MOVE profiles share one type-free native template. The graphical reader
  matches a MOVE box to the first MOVE profile by template shape, so a readback can
  report another MOVE type's profile id. The builder itself selects the profile by
  its exact type.
