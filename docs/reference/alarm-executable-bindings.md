# Supplied alarm operands and executable bindings

`diagnostic_plan` can check explicitly supplied alarm operand observations against
the selected saved FB declaration, instance and captured call arguments. The
result is retained in the diagnostic case and carried into watch and trace plans.
This is an offline source association. It does not prove native alarm generation,
execution, causality, equipment identity or complete consumer coverage.

## Required source evidence

Use an imported saved review with the original block exports and exact resolved
graph endpoints. Select an alarm, supervision or HMI alarm through `alarmContext`
and an executable FB instance through the existing `instance` field. The selected
operand must be a nonvirtual `member` node in that instance's exact PLC and scope.
Its metadata may contain this normalized observation:

```json
{
  "alarmOperand": {
    "schemaVersion": "alarm-operand.v1",
    "role": "condition",
    "blockPath": "/projects/Example/plc/blocks/FB_Conveyor",
    "instance": {
      "dbPath": "/projects/Example/plc/blocks/DB_Line",
      "memberPath": ["Conv2"]
    },
    "memberPath": ["Enable"],
    "datatype": "Bool"
  }
}
```

These paths are illustrative; use exact paths and node IDs from your saved
review. This is a TIA MCP supplied-data profile, **not a Siemens export format**.
No native producer is implemented for this profile. Supported roles are
`condition`, `delay`, `parameter` and `status`. Original metadata remains retained.
Unknown profiles remain unavailable; malformed recognized observations remain
invalid. Supplied `nativeConfirmed` edge labels do not authenticate the source.

## Explicit selection

Add `alarmBindings` to the existing diagnostic request. Each entry selects an
existing diagnostic signal and the original directed edge positions connecting
the selected alarm context to an exact operand:

```json
{
  "alarmBindings": [
    {
      "id": "enable-condition",
      "signalId": "enableSource",
      "operand": {
        "nodeId": "condition-member",
        "objectPath": "/projects/Example/plc/operands/condition"
      },
      "edgeIndices": [1, 4]
    }
  ]
}
```

Indices address the original review's `model.edges` array, not a filtered view.
The ordered chain must start at `alarmContext` and end at the operand, use exact
resolved endpoints and contain no cycles. Supported edge kinds are `dependsOn`,
`reads` and `hmiBinding`. Merely providing `alarmContext` never selects storage.

The selected FB member and diagnostic signal must have the supplied datatype.
The existing source resolver checks formal instance storage and simple captured
call arguments, including supported IN_OUT aliases. Repeated calls remain
separate evidence; they do not identify which call ran. Arrays, indirect accesses,
opaque expressions and incomplete call resolution do not become inferred matches.

## Results and refusal behavior

The optional `alarmBindings` field uses `diagnostic-alarm-bindings.v1` in saved
cases, watch envelopes and trace specifications. It retains the selectors,
original operand/edges, signal, declaration source and bounded storage candidates.
Each row reports a declaration match or a specific unavailable, invalid,
owner/type/storage mismatch or incomplete-resolution status.

Watch and trace handoff require **every explicitly selected binding** to match;
otherwise they return `AlarmBindingUnresolved`. They independently reconstruct
the report from the original review first, so altering its evidence and computing
a new content hash does not bypass the checks. Replanning also revalidates the
predecessor. Provide fresh `alarmContext` and `alarmBindings` to select bindings
for the new case; omission retains old associations only in predecessor lineage.

Limits: 16 selections, eight distinct edges per selection, eight instance-prefix
components, sixteen formal-member components, 64 call sites, 256 parameter rows,
64 storage candidates, 1,000 resolution visits and a 512 KiB report. Relevant
resolution gaps or truncation prevent a match, even if one candidate is known.
Capacity refusal preserves the original saved review.

`sourceAuthenticityVerified`, `nativeQualified`, `runtimeAlarmAssociationVerified`
and `coverageComplete` remain false. [Runtime captures](runtime-captures.md#saved-alarm-operand-bindings)
can retain and revalidate these bindings, including unselected signals, without
granting online authority or assertion admission.

## Verification and remaining work

See [the increment evidence](../verification/alarm-executable-bindings-20261018.md).
Complete UDT/consumer mapping, genuine vendor captures/native producers, live
acquisition association and AL lifecycle operations remain open. TIA/PLCSIM and
real equipment tests must establish native behavior independently.
