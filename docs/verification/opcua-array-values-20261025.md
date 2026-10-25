# OPC UA array and matrix implementation

The read-only runtime client now returns bounded arrays and matrices through manual
reads, polling and subscriptions. JSON/CSV exports and reopened journals preserve
the same envelope. No new dependency, native worker or process-value write is added.
See [representation and bounds](../reference/opcua-runtime.md) and the
[source-hash receipt](opcua-array-values-20261025.json).

The encrypted loopback suite passes 600 checks. Coverage includes exact Int64 and
UInt64 extrema, row-major matrix order, empty arrays/matrices, null Variants and
nullable String/ByteString elements, scalar ByteString versus Byte arrays, original
quality/timestamps, malformed declared shapes, honestly rehashed retained-data
mutations, 4,096-element capacity, JSON expansion limits and credential echoes
inside arrays. Unsupported elements refuse the entire value; bad values remain
suppressed. Declared dimensions remain separate from observed shape.

The real Host MCP checks pass for sessions/reads (66), polling (161) and
subscriptions (341), including complete artifact transfer, CSV/JSON preservation,
restart retrieval and no acquisition replay. The 26 OPC UA Python tests, contract
coverage check and 46 offline diagnostic groups also pass. Check counts that include
retained files can vary with the fixture run; receipts retain the exact counts.

Original local receipts and artifacts are retained outside the task checkout at
`D:/Entwicklung/TIA_MCP-public/artifacts/opcua-arrays-20261025`. The final runtime
suite is `run7`; MCP receipts are `mcp-runtime-2`, `mcp-capture-2` and
`mcp-subscription-2`. Subsequent runs include the new decoder in the harness's
source-hash inventory; the original receipts already hash the executed binaries.

These are local synthetic OPC UA server checks. Native Siemens configuration,
physical CPU/firmware compatibility and release acceptance remain unqualified.
Structured records, Variant arrays, reconnect and process-value writes remain
outside this change.
