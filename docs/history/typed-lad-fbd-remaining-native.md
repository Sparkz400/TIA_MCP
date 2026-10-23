# Typed graphical native qualification: checkpoint and remaining work

Historical record. See [current status](../STATUS.md) and [remaining TODO](../TODO.md) for the latest checkpoint and work order.

This records the native-profile workstream checkpoint at the user's request to
finish up. The final active case completed at **2026-10-08 13:51:22 UTC**. No next
capture batch or advanced route qualification batch was started. This document
does not declare the full LAD-010 through LAD-070 workstream complete.

## Completed route builder qualification

Each case below made actual MCP requests and independently checked native import
and compilation, complete native XML preservation, save/reopen, and explicit
`change_restore` checkpoint recovery. All four native gates passed. **Runtime
is false for every case**: these route cases explicitly used engineering-only
mode. Every creation-run host exited with code 0 and no cleanup failure.
The owned Openness connections were detached; the adapter does not certify
native Portal process exit (`processExitVerified=false`).

| Route | Language | Operation | Exact report |
|---|---|---|---|
| Default software-unit namespace | LAD | Replace Boolean network | `artifacts/tqr02/typed-unit-lad/result.json` |
| Default software-unit namespace | FBD | Replace Boolean network | `artifacts/tqr02/typed-unit-fbd/result.json` |
| Alternate namespace | LAD | Replace Boolean network | `artifacts/tqr02/typed-namespace-lad/result.json` |
| Alternate namespace | FBD | Replace Boolean network | `artifacts/tqr02/typed-namespace-fbd/result.json` |
| Default software-unit namespace | LAD | Create FC | `artifacts/tqr03/typed-unit-lad-create-fc/result.json` |
| Default software-unit namespace | LAD | Create FB | `artifacts/tqr03/typed-unit-lad-create-fb/result.json` |
| Default software-unit namespace | FBD | Create FC | `artifacts/tqr03/typed-unit-fbd-create-fc/result.json` |
| Default software-unit namespace | FBD | Create FB | `artifacts/tqr03/typed-unit-fbd-create-fb/result.json` |
| Alternate namespace | LAD | Create FC | `artifacts/tqr03/typed-namespace-lad-create-fc/result.json` |
| Alternate namespace | LAD | Create FB | `artifacts/tqr03/typed-namespace-lad-create-fb/result.json` |
| Alternate namespace | FBD | Create FC | `artifacts/tqr03/typed-namespace-fbd-create-fc/result.json` |
| Alternate namespace | FBD | Create FB | `artifacts/tqr03/typed-namespace-fbd-create-fb/result.json` |

The replacement run used immutable worker `artifacts/typed-native-build-04`.
The creation rerun used `artifacts/typed-native-build-06`; both used
`artifacts/typed-host-build-01/TiaMcp.Host.exe`. Each case retains the binary,
resource and request evidence in its report/output directory. Native fixtures
and typed requests are in `fixtures/typed-graphical/route-cases.v1.json`.

The earlier eight creation failures in `artifacts/tqr02` remain intact, each
with successful explicit restoration and full baseline verification. Their
cause was forcing `AutoNumber=false` on a software-unit block. The independent
raw native probe `artifacts/tgc01/report.json` records FC/FB acceptance with the
captured `AutoNumber=true` and
rejection when false, including exact complete readbacks and compiler results.
The successful rerun supersedes those failures; it does not erase them.

## Independent native evidence retained

`fixtures/typed-graphical/profiles.v1.json` currently contains 98 source export
records, 58 instruction profiles (36 standard, 13 software-unit, 9 alternate
namespace), 12 creation templates, four global FB instance templates and four
multi-instance templates. These counts include reader-only profiles. Native
source acceptance is separate from typed builder or runtime qualification.

The source/native captures use V21 Update 2, Step7 API file version
`2100.0.121.1`, FlgNet v5, Interface v5, and the exact configured CPU
`OrderNumber:6ES7 511-1AL03-0AB0/V4.1`. Other CPU/version combinations are not
implicitly qualified.

- Standard reference corpus: `artifacts/tgp04` through `tgp11`, with individual
  source/capture references and hashes in the profile catalog. This includes
  both graphical languages, Boolean variants, six `Int` comparisons, calls,
  timers, counters, local/global IEC storage, 17-scalar declaration templates,
  numeric FC/FB parameter flow and persistent FB state.
- Separate software-unit and alternate-namespace Boolean and creation-envelope
  source captures: `artifacts/tgr01/capture.json` and
  `artifacts/tgr02/capture.json`.
- Default-unit numeric callees: successful entries in
  `artifacts/tgr03/capture.json`. Default-unit fully qualified global DB and
  multi-instance storage declarations: successful entry in
  `artifacts/tgr04/capture.json`.
- Default-unit Eq, numeric FC, local FB and global FB graphical callers in
  **both** languages: eight successful entries in
  `artifacts/tgr05/capture.json`. Raw exports are checked into
  `fixtures/typed-graphical/native/TgpUnit_*.xml` with exact catalog hashes.
- Standard direct dependent instance DB before/after proof:
  `fixtures/typed-graphical/instance-effects.v1.json`, bound to
  `artifacts/tgp09/capture.json` and `artifacts/tgp10/capture.json`. The native
  DB retains explicit `RetainedMarker=42` and its complete existing payload
  while appending the observed 17-scalar `Child` declaration.

Observed unit call encoding is `CallInfo Name="TiaMcp_UnitA.TgpUnit_NumericFB"`
without a Namespace attribute. Local instances retain one `Component
Name="Child"`; global instances use one component containing the full qualified
name, e.g. `TiaMcp_UnitA.TgpUnit_NumericFB_DB`. These observations do not prove
the separate encoding of ordinary global DB operand accesses.

## Precise blockers and historical failures

1. Unit SIMATIC SD IEC probes compile with native **Missing instance DB**:
   `TgpUnit_CTD_FBD` in `artifacts/tgr04/capture.json` and the isolated repeat
   `artifacts/tgd01/capture.json`; `TgpUnit_TON_Global_LAD/FBD` in
   `artifacts/tgr05/capture.json`. Export then rejects the inconsistent block.
   No successful local/global IEC instruction profile was published for either
   scoped route. Alternate-namespace IEC source/native probes are unexecuted.
2. Those partially applied source failures have no after-fingerprint.
   `change_restore` rejects with `StaleRevision`; the stale guard was not
   weakened. In tgr05, explicitly authorized `checkpointRetrieveRecovery`
   retrieved the exact SHA-bound checkpoint into a new owned instance and
   verified every known native payload. The failed original disk projects and
   archives remain preserved. This fallback is **not** a passed
   `change_restore` or typed recovery gate.
3. Earlier bare custom-type names in unit SCL failed; qualified type names then
   compiled successfully in tgr04. Earlier multi-block `.s7dcl` input only
   imported its first block. Subsequent SD fixtures use one declaration per
   source. Keep the old failure reports as diagnostics.
4. Numeric scoped call/instance builder cases are authored but unexecuted.
   Qualified native references are available for the default unit only; a
   native reference compiling is not a typed builder pass.
5. No scoped runtime assertions were executed. Runtime work must preserve the
   existing owned simulator infrastructure and distinguish prerequisites,
   compilation, XML preservation and actual assertion outcomes.

Siemens' [V21 software-unit rules](https://docs.tia.siemens.cloud/r/en-us/v21/using-software-units-s7-1500/creating-a-plc-program-in-software-units-s7-1500)
require automatic numbering and permit IEC timers/counters. The SD failure
therefore does not establish that the native target generally prohibits IEC
instructions.

## TODO: resume only after a new request

1. Execute the 12 existing default-unit advanced cases in
   `fixtures/typed-graphical/route-advanced-cases.v1.json`: Eq, numeric FC,
   existing multi/global numeric FB, and explicit new multi/global numeric FB
   storage, each in LAD and FBD. Immutable worker07 includes qualified
   CallInfo encoding and scoped storage/template selection:

   ```powershell
   python scripts/qualify_typed_graphical.py --cases fixtures/typed-graphical/route-advanced-cases.v1.json --host artifacts/typed-host-build-01/TiaMcp.Host.exe --worker artifacts/typed-native-build-07/TiaMcp.Openness.V21.exe --output artifacts/typed-route-advanced-resume-01
   ```

2. Continue independent route reference capture. `artifacts/tgr06.inputs`
   contains **38 unexecuted inputs**, SHA-256
   `5037ccc063e8646641003f5cfa8c68d93869e218b75d1a33b03da0f5a26eee25`.
   It starts with alternate-namespace numeric callees/storage/callers/Eq, then
   explicit native XML TON probes and remaining comparison references.
   The last clean owned project is recorded in
   `artifacts/tgr05/capture.json` at `currentProject`; it was detached cleanly.
   The original source checkpoint and its hash are retained below:

   ```powershell
   python scripts/capture_typed_graphical_routes.py "@artifacts/tgr06.inputs" --host artifacts/typed-host-build-01/TiaMcp.Host.exe --worker artifacts/typed-native-build-07/TiaMcp.Openness.V21.exe --output artifacts/tgr06 --archive artifacts/tgr04/changes/change-5174ee4f86a946e69ecd2b237817c7b2/before.zap21 --sha256 82cdd36a64784306bb2294c7b45fe8c614ec2489fbb01a7f2edd984433b753f9 --resume-report artifacts/tgr05/capture.json --retrieve-after-restore-rejection
   ```

   Keep one active native portal per agent. Do not use or modify unrelated
   projects. The fallback flag has the limited meaning described above.

3. The `route-*-native-*.xml` files are **unexecuted diagnostic probes** produced
   independently of the typed builder from already observed standard/native
   route templates. Each has a `.provenance.json` input-hash/field-change record.
   They are not represented as TIA-authored SD programs and are not in the
   validated profile catalog. If native import/compile/export succeeds,
   capture all requested IEC families/storage variants independently, append
   their exact route profiles, rebuild/pin deployed resources, and execute the
   corresponding typed cases. If it fails, retain the precise diagnostics.
4. Capture route-specific direct DB before/after effects with the prepared
   `sources/route-{Unit,Namespace}-instance-effect-{before,after}.scl` fixtures.
   The route capture script supports explicit `--expected-existing-change`
   caller/DB names and `--observe-block` for the dependent DB. After the before
   phase, resume that owned report for the after phase; retain full exports.
   `scripts/extract_typed_instance_effects.py` accepts `--route`, `--prefix` and
   `--namespace`, producing separate route evidence rather than replacing the
   standard manifest. No scoped effect pair has been captured yet.
5. Re-run `fixtures/typed-graphical/author_advanced_route_cases.py` after new
   captures. Its separate `route-pending-references.v1.json` currently records
   **80 unqualified requested cases** with missing native references; they are
   not silently counted as passes. Append profiles with
   `scripts/extract_typed_graphical_profiles.py --append` to preserve existing
   qualification evidence. Do not copy a standard profile into another route
   without native acceptance in that route.
6. Ordinary namespaced global DB operand encoding remains uncaptured. No new
   source fixture or capture batch for that additional diagnostic was started
   after the stop request. Preserve the existing explicit diagnostic until a
   separately authored reference proves its native Symbol representation.

The source/probe generators, capture scripts and effect extractor were Python
syntax-checked. The latest offline checks passed: nine typed-contract/provenance
tests and three direct-instance-evidence tests. These are engineering checks,
not native or runtime qualification.
