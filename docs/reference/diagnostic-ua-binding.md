# Prebound UA diagnostic observations

`diagnostic_ua_binding_plan` saves explicit observation intent for an existing
diagnostic experiment before a new UA polling capture or subscription starts.
It is an offline operation: it opens no session and performs no Siemens operation.

Prepare an experiment with `diagnostic_experiment_plan`, then select a new or
existing UA session attempt ID and exact nodes:

```json
{
  "experimentId": "<diagnostic_experiment_plan.id>",
  "request": {
    "attemptId": "<exact lowercase 32-hex UA session attempt ID>",
    "reason": "Observe the selected conveyor enable through the supplied UA node.",
    "mappings": [
      {
        "signalId": "enable",
        "node": {
          "namespaceUri": "urn:example:machine",
          "identifierType": "string",
          "identifier": "Conveyor.Enable"
        },
        "unit": "bool",
        "reason": "Caller-selected observation corresponding to the saved enable declaration."
      }
    ]
  }
}
```

The returned `id` identifies an immutable `diagnostic-ua-binding.v1` artifact.
The artifact retains the original experiment, exact declaration for each selected
signal, mapping reasons and explicit units. Select 1–64 distinct resolved signals
and distinct URI-based nodes. A subset of experiment signals is permitted;
omitted signals do not become observations. Namespace indexes and unknown fields
are rejected. Units are caller-supplied labels, not a numeric conversion or a
server engineering-unit claim.

Pass this ID as `request.diagnosticBindingId` to a new
`runtime_ua_capture_start` or `runtime_ua_subscription_start`. Use exactly the
binding's session attempt and ordered node list. Each new start reopens the
original experiment, watch/trace plans and saved review before leasing the UA
session or publishing capture intent. Missing evidence, changed declarations,
different sessions, changed nodes and reordered nodes refuse. Ordinary raw
captures may still omit the binding. The optional field is omitted when null,
preserving previous normalized request bytes.

The exact binding ID is retained inside the original capture request. Repeating
a capture ID observes that original intent, including after Host restart; it
does not start acquisition again. Adding, removing or changing a binding on an
existing capture refuses as a changed request. Raw status/page/export remains
available even if the separate diagnostic source is later unavailable. This
does not certify that the diagnostic source remains available for a report.

The binding expresses caller selection only. A saved PLC `UInt` declaration and
a node returning `UInt64` remain distinct original facts; no automatic cast or
datatype equivalence is inferred. The binding verifies no online PLC address,
running program, physical device, source authenticity, clock synchronization or
behavioral assertion. Existing capture quality, sequence, timing and cleanup
uncertainty retain their original semantics.

[Retained observation reports and Explorer integration](diagnostic-ua-observations.md)
consume this pre-acquisition intent. They preserve quality/timing uncertainty and
leave hypotheses unresolved; this binding does not itself produce a verdict.

Contracts: [binding](../../contracts/diagnostic-ua-binding.v1.schema.json),
[polling](../../contracts/opcua-runtime-capture.v1.schema.json),
[subscription](../../contracts/opcua-runtime-subscription.v1.schema.json).
