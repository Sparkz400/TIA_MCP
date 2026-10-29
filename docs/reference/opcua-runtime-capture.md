# Retained OPC UA polling capture

The five `runtime_ua_capture_*` tools acquire explicit nodes through an already connected, owned `runtime_ua_connect` session. They reuse the durable runtime capture journal: immutable hash-linked segments, an atomically published committed head, bounded reservations and exact exports. UA acquisition has a separate store under `runtime-ua-acquisition/runtime-captures`, separate status/export/page schemas and explicit `plan.uaContext`; existing Web capture records remain unchanged.

1. Connect using the [encrypted pinned UA session workflow](opcua-runtime.md).
2. Call `runtime_ua_capture_start` with a new retained lowercase 32-hex `captureId` and `request` containing `attemptId`, exact `nodes`, `intervalMilliseconds`, and `durationSeconds`. Optional `maximumSamples` counts individual node read attempts; optional `maximumBytes` bounds the journal. A repeated identical capture ID only observes its original intent; a changed request refuses.
3. Inspect `runtime_ua_capture_status`, then page its immutable `snapshotRef` through `runtime_ua_capture_page`. Use `runtime_ua_capture_stop` for an explicit partial result.
4. Export `json` or `csv` with `runtime_ua_capture_export`; CSV always has a JSON companion. Download each returned hashed path through `artifact_read`.
5. Inspect and explicitly disconnect the original UA session. Stopping acquisition does not log out. Cancellation during an in-flight service can leave `remoteSessionUnknown`; that state is not silently recovered.

## Exact acquisition semantics

Optional `request.diagnosticBindingId` selects an immutable
[diagnostic observation binding](diagnostic-ua-binding.md) before a new capture.
Its exact session attempt, ordered nodes and original experiment are revalidated
before session acquisition. Existing unbound captures cannot be rebound.

Nodes are read sequentially, once per admitted polling cycle, with the existing credential and namespace/server/start-time identity checks before each read. A selection ID is derived from the exact namespace URI, canonical identifier type and identifier; namespace indexes are never durable node identities. Requested polling intervals are not server sampling intervals and are not scan synchronization. A delayed read skips elapsed polling slots instead of catching up; gap rows retain the skipped count. Partial final cycles remain partial.

Each read row contains the complete original decoded UA read-result JSON as a string. This preserves Int64/UInt64 decimal strings, type identity, rank/dimensions, raw 32-bit status, Good/Uncertain/Bad classification, nullable source/server timestamps and picoseconds. Bad values remain suppressed; uncertain values remain uncertain; unsupported forms remain explicit. Retained scalar and array validation enforces canonical decoded builtin/encoding pairs and numeric ranges, exact array counts/order and declared versus observed shape. JSON and CSV retain the complete array envelope; reopen revalidates it without acquiring new values; a custom declared DataType remains a separate original declaration. Nothing is interpolated, no timestamp is invented, and client wall-clock ordering is not substituted for source-clock agreement. Separate monotonic elapsed/scheduled times describe host acquisition only.

Any service failure ends this capture; per-item bad/unsupported results remain successful transport observations. Duration expiration, explicit stop, Host shutdown, sample/byte/value limits and storage failure have distinct terminal reasons. A publication failure can leave an uncommitted tail; reopen exposes only the verified committed prefix and unknown loss. Retained hashes establish byte consistency, not source authenticity, device identity, or an executable PLC/program/address association.

## Limits and lifecycle

Limits are 1–64 unique nodes, intervals 100–60,000 ms, duration 1–3,600 seconds, 1–100,000 individual read attempts, 1–64 MiB per capture, a 128 KiB plan, 64 retained captures and 512 MiB in the separate UA capture store. At most four workers run, with one per session. Pages contain 1–200 rows under the existing byte budget; oversized individual rows are visibly omitted from pages and available in complete exports. There is no automatic eviction.

Capture stop cancels this Host's own worker. Host shutdown stops workers before closing its captured UA sessions, even when capture publication fails. An actual Host crash does not manufacture a completed terminal segment: another Host reports `interrupted`, keeps the original bytes, and performs no network call on status/page/export/repeated start/stop. The UA session separately remains `hostEndedRemoteUnknown` until explicit local acknowledgement or independently established cleanup; acknowledgement does not prove remote logout.

This profile provides polling only. [Bounded UA subscriptions](opcua-runtime-subscriptions.md) separately retain monitored-item revisions and Publish/Republish evidence. Subscription transfer and implicit reconnect remain unsupported; bounded same-session recovery requires the explicit option below. There is no subscription-to-polling relabeling, diagnostic behavioral verdict, physical-device qualification, runtime write, or native TIA operation. Polling reserves the session against competing acquisitions and manual read/browse/disconnect until the capture releases its lease.

Contract: [opcua-runtime-capture.v1](../../contracts/opcua-runtime-capture.v1.schema.json). Qualification is exercised by the existing managed UA suite plus `scripts/qualify_opcua_capture.py`; the latter starts an actual encrypted loopback server and Host, verifies exact values independently, downloads artifacts, crashes its original owned Host, and reopens without replay. The [real-server runs](../verification/runtime-real-server-20261029.md) (`local.runtime.plcsim-tcpip.20261029`) repeat a polling capture with page and export against the OPC UA server of an owned PLCSIM instance in TCP/IP mode.

## Optional transport recovery

Set `maximumReconnectAttempts: 1..3` in the original start request to enable
[bounded same-session reconnect](opcua-runtime.md#controlled-acquisition-reconnect).
Omission or zero retains the original stop-on-error behavior. The failed read stays
in the journal; recovery continues at a future scheduled polling cycle and records
the skipped slots. It never replaces the failed read with a retry result.

Recovery boundaries are `gap` rows with `skippedPolls: 0` and an additional
`reconnect: {attempt, state, errorCode}` object. Ordinary gap rows still report the
number of skipped polling slots. JSON preserves both forms; CSV for opted-in
captures adds a `reconnect_json` column. Existing captures retain their original
CSV layout and serialized bytes. Status and reopened evidence never initiate
network recovery.
