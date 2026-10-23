# Explicit integer text-list domain conversion

`hmi_text_list_generate_set` accepts `unified-text-list-set-request.v3` through
its existing committed `uploadRef` argument. Reopening uses the existing
`recordId` argument. V3 extends the retained set-evolution workflow with an
explicit offline conversion profile; v1/v2 requests retain their previous
semantics and original records remain readable.

For an existing numeric output, retain `operation: "update"`,
`bindingAction: "preserve"`, all output/file/list/library identities, and the
exact before/after member selection. Set the template's `dataType` to the
captured after declaration and supply:

```json
"typeConversion": {
  "profile": "integer-value-preserving.v1",
  "fromType": "Int",
  "toType": "USInt"
}
```

The profile admits exact `SInt`, `USInt`, `Int`, `UInt`, `DInt` and `UDInt`
declarations, covering signed/unsigned 8-, 16- and 32-bit domains. Both types
must differ. The predecessor's captured after type and the new review's
captured before type must equal `fromType`. The new captured after type,
declared HMI type and template type must equal `toType`. Missing, ambiguous,
aliased, floating-point, bit-string and 64-bit types refuse this profile.

Every original listed interval must fit the target domain. The complete
authoring intent stays equal, including each state ID, range endpoint, range
kind, default flag, translation, language order and output identity. A list
covering values 0–127 can therefore move from Int to USInt; a range containing
−1 or 256 cannot. Removing or changing the offending range does not conceal
overflow: comparison with the original predecessor produces a conflict.
Neither wrapping, clamping, scaling nor state remapping is implemented.

This checks the **listed text-list values only**. It performs no PLC cast,
value write, native import or binding update. A successful narrowing can still
have `fullSourceDomainRepresentable: false`. Unlisted values, default handling,
native numeric behavior and runtime binding compatibility remain unverified.
The YAML pair is unchanged because Decimal entries retain the exact values;
the new record captures the independently observed declaration change and
explicit checked intent.

Each v3 output retains `typeConversion` evidence, or null for ordinary
evolution. The evidence includes both domains, per-state representability,
`allListedValuesRepresentable`, `fullSourceDomainRepresentable`, and the
original source binding conflicts. The generic type-change warning remains
visible there and in the captured binding. Only that precise warning is
handled by this explicit profile; unrelated conflicts still suppress the
entire set. Effects retain the explicit conversion selection and
`plcValueConversionPerformed: false`.

Complete baseline/current pairs must still equal the predecessor's exact
files. Manual drift, missing companions, stale component/member identity,
manual ownership, independent proposed-file disagreement and retained-peer
changes prevent publication. Ordinary v3 output additions, removals, renames
and same-type rebindings retain v2 behavior; they cannot be combined with a
conversion on that output. Use v3 to continue or remove outputs from a v3 set.

Requests that lack or mistype required conversion fields, contain unknown
options, use unsupported types, or specify target-out-of-range template
values return `InvalidHmiComponentReview`. Validly shaped but contradictory
lineage/evidence requests publish a `conflict` record with no candidate files.
No automatic retry or native execution occurs.

The [v3 schema](../../contracts/unified-text-list-set.v3.schema.json) describes
requests, immutable records and receipts. Existing 16-output, 32-file,
64-MiB output/peer, 4-MiB input, 16-MiB record, 16-predecessor and 128-MiB
lineage limits apply. Retained v1/v2/v3 ancestors and every original source are
revalidated on reopen.

Focused qualification uses the HmiComponents test executable's
`--integer-conversion <fresh-directory>` selector. Its
`text-conversion/mcp-fixture.json` is the input to
`scripts/test_hmi_integer_conversion_host.py --host <Host.exe> --fixture
<fixture.json> --output <fresh-directory>`. The harness uses disabled native
workers, independent XML/domain/YAML checks, actual MCP upload/reopen,
restart/tamper controls and original prior-producer v2 fixture replay.
