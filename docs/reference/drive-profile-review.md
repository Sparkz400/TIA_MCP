# Supplied drive parameter and telegram review

`drive_profile_review` compares supplied parameter observations or an offline
proposal against an exact saved hardware selection. No Siemens product is needed.
The input is this project's explicit observation format, **not a Siemens export
format**. There is no native adapter, parameter download or motion command here.
The separate [`drive_observe`](../guides/drives.md) tool reads offline Startdrive
drive objects into v2 snapshots of this format.

1. Import or reuse an exact saved review. Select one nonvirtual hardware
   `objectPath`; a same-name object or display folder cannot substitute for it.
2. Upload a `drive-profile-review-request.v1` or explicit `v2` JSON document with
   `artifact_upload_begin/chunk/commit`.
3. Call `drive_profile_review(reviewId, uploadRef)` and retain its `recordId`,
   original `input` and complete `reportArtifact` references.
4. Reopen with `drive_profile_review_page(recordId, section, offset, count)`.
   Sections are `summary`, `observations` and `changes`; v2 also supplies
   `telegrams` and `telegramChanges`. V1 refuses the new sections. Use `artifact_read` for
   complete originals/reports; upload release and Host restart preserve them.

The strict [v1 contract](../../contracts/drive-profile-review.v1.schema.json) defines:

- Request: schemaVersion, exact reviewId, hardwarePath, afterKind
  (`observation` or `proposal`), before and after snapshots.
- Snapshot: identity, valueDomain (`offline` or `online`), supplied
  parametersComplete flag, and parameters.
- Identity: a supplied driveId (a local evidence handle, not a discovered native
  identity), family, orderNumber, firmware, parameterSet and unknownReason.
  Family/order/firmware can be null with a nonblank reason; parameterSet null
  preserves unavailable/not-applicable information. Both complete identity
  declarations must match exactly. Cross-profile/template mapping is not inferred.
- Parameter: exact full name, number, arrayIndex (-1 for non-array, otherwise
  0..32767), nullable parentName, nullable unit, access, value and opaque metadata.
  Unit null is unknown; empty text remains distinct. Access is a supplied
  `readOnly`, `readWrite` or `unknown` observation.
- Value: scalar `{kind:"scalar",type,text}`; BICO-style reference
  `{kind:"reference",driveId,parameterName,reason}`; or unavailable
  `{kind:"unavailable",reason}`. Missing reference selectors require a reason.
  Null never silently means zero, disconnected or a valid missing source.

Full parameter names match ordinally. Number/index/parent changes under the same
name report `selectorConflict`; they do not authorize a rebind. Collection order
never identifies a bit: a sparse list's third entry need not be bit 2. Exact local
BICO references resolve only to supplied rows on the declared drive. Unknown or
other-drive references remain unresolved/outside the profile. Parent chains are
explicit supplied relationships with matching number/index, cycle checks and a
128-link traversal bound; they are not proof of native bit membership.

Observations retain offline/online domains separately. Proposals require both
domains to be offline. Changing a value whose **before** access is readOnly
reports `contradictsSuppliedReadOnly`, even if the after row says readWrite.
Unknown access reports `accessUnknown`. Every other proposal remains
`notAssessed`: supplied readWrite never means approved. Missing rows report only
`onlyBefore` / `onlyAfter`, not native creation or deletion.

Scalar type/text, units, limits, enum dictionaries and vendor metadata remain
unchanged. Metadata is opaque and compared structurally; scalar text is compared
exactly, without numeric/culture/unit conversion. These inputs cannot establish
native constraints or dependent effects. All native execution/qualification,
source authenticity, freshness, drive identity, constraint and whole-drive
completeness flags remain false, even when supplied coverage flags are true.

## Explicit v2 telegram observations

The [v2 contract](../../contracts/drive-profile-review.v2.schema.json) preserves
the parameter behavior and adds `telegramsComplete` and `telegrams` to each
snapshot. It does not reinterpret opaque parameter metadata. The existing v1
request/report/receipt bytes remain readable with their original semantics.

Each telegram declares an exact local `id`, nullable `type`, `number`,
`inputBytes` and `outputBytes`, `addressesComplete`, `addresses` and
`unknownReason`. These are supplied observations, not discovered native handles.
The supported type names follow the V21
[TelegramType reference](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/references/telegramtype).
Unknown types use null and an explicit reason. Directional byte sizes correspond
to the documented unit of `GetSizeInBytes`; no fixed signal layout is inferred
from a number. A main telegram numbered 999 is marked freely configurable.
[Telegram reference](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/references/telegram).

An address declares its own exact `id`, nullable `plcPath`, `direction`
(`Input`/`Output` or null), `context`, `startAddress`, `length`, independent
`addressUnit` and `lengthUnit` (`bit`/`byte`/`unknown`), and `unknownReason`.
Unknown values, unknown units or incomplete address capture require a reason.
Context labels remain uninterpreted. The
[address reference](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/references/addresscomposition)
names start and length properties without specifying their units; this format
therefore requires explicit supplied units instead of assigning them by default.

Known bit/byte quantities normalize to exact Int64 bit coordinates, including
an exclusive range end. Unknown units produce null normalized values. A known
length is compared with the explicitly declared directional byte size; equality
or difference is an arithmetic observation, not a geometry/compatibility verdict.
PLC resolution requires one nonvirtual saved PLC with a unique exact node ID.
It does not verify ownership, live wiring or identity across two PLCs.

Before/after rows correlate only by their supplied IDs. Telegram and address
collection order does not manufacture a change; missing IDs remain onlyBefore
or onlyAfter. Equal numbers, types or addresses never merge rows. Reports retain
the exact original row and array index, explicit unknowns and false native flags.
Completeness is supplied and cannot certify all telegrams or affected consumers.
This format does not check firmware eligibility, signal layouts, address overlap,
telegram/encoder compatibility, safety or rebind validity.

Limits: 4 MiB strict UTF-8 JSON, depth 32, 4,096 parameters per snapshot; 16 MiB
report, 512 MiB retention including incomplete publications; pages contain 1..200
rows within 128 KiB. An oversized row is explicitly omitted from its page and
remains in the full report/original. Duplicate JSON keys and parameter names,
invalid selectors, mismatched profiles and corrupted references refuse. Reopen
rederives the report from retained input and exact saved source before returning
it. Interrupted publication may leave an input without a final receipt.
V2 additionally permits up to 128 telegrams per phase and 16 addresses per
telegram. Values are nullable nonnegative Int32; explicit conversion and summed
ranges use Int64. Unknown fields and duplicate exact telegram/address IDs refuse.

The [full Startdrive plan](../plans/expansion-plans/startdrive.md) retains native
capture, further typed constraints, templates, telegram/TO/DRIVE-CLiQ integration, transfers,
security, persistence, activation and specialist acceptance as open work.
