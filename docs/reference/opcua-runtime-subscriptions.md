# Bounded OPC UA subscription acquisition

`runtime_ua_subscription_start`, `status`, `stop`, `page` and `export` operate on the existing encrypted, pinned, owned `runtime_ua_connect` session. The client explicitly invokes CreateSubscription, CreateMonitoredItems, Publish, bounded Republish and DeleteSubscriptions. It does not register an SDK background subscription, transfer, call methods, or write process values. Optional `maximumReconnectAttempts: 1..3` permits [bounded same-session recovery](opcua-runtime.md#controlled-acquisition-reconnect); the default is zero. Durable local evidence is separate from the OPC UA server's optional durable-subscription feature; SetSubscriptionDurable is not invoked.

## Acceptance and workflow

The milestone's acceptance is an actual encrypted independent loopback fixture, exact requested/revised settings and item failures, original typed notification data/status/times, sequence/duplicate/overflow/retransmission evidence, bounded lifecycle, exact exports, crash/reopen without replay, and compatibility with existing polling/session workflows. Native TIA and physical-device acceptance are separate.

1. Establish an owned encrypted session with the [session workflow](opcua-runtime.md). Anonymous and protected username sessions are supported.
2. Start with a new lowercase 32-hex `captureId` and `request.attemptId`, plus explicit URI-based `request.nodes`. Optional request settings are publishing interval, sampling interval, queue size/discard policy, keepalive/lifetime counts, duration, maximum notifications, bytes and retransmissions.
3. Read status, then page its immutable `snapshotRef`. Revised settings and individual monitored-item statuses are in the `created` event, alongside the original per-node metadata observation. A failed item does not erase successful neighbors. All failed items terminate explicitly.
4. Stop explicitly or wait for a terminal bound. Stop waits for the current bounded service and attempts DeleteSubscriptions on the original session. `recordedProgress.remoteCleanupConfirmed` is independent of local terminal state. Check the separate session status; uncertain cleanup never becomes confirmed logout.
5. Export JSON or CSV. CSV has a complete JSON companion and retains each entire event as JSON. Download hashed artifact paths through `artifact_read`.

A repeated identical capture ID observes original retained intent. Changed intent refuses. Another Host opens evidence only; it never recreates, transfers or resumes the subscription. Connect/disconnect retain the original session's separate reservation and unknown-remote-state semantics.

Unknown members of the subscription `request` or its nested `nodes` refuse during MCP deserialization before session access or capture intent. This includes unsupported `reconnect`/transfer/write/resume options (the supported bounded option is `maximumReconnectAttempts`) and misspelled settings. The guard is scoped to subscription requests; existing shared read/poll node deserialization and the MCP SDK's handling of unbound outer tool arguments are unchanged.

## Evidence and ordering

Optional `request.diagnosticBindingId` selects an immutable
[diagnostic observation binding](diagnostic-ua-binding.md) before a new subscription.
Its exact session attempt, ordered nodes and original experiment are revalidated
before session acquisition. Existing unbound subscriptions cannot be rebound.

Each notification retains the complete decoded scalar or bounded array/matrix representation used by existing UA reads: exact Int64/UInt64 decimal strings, builtin and declared type, scalar rank/dimensions, raw 32-bit status and quality, original nullable source/server timestamps, and both picosecond fields. Bad values are suppressed, uncertain values remain uncertain and unsupported element types/structures stay explicit. Arrays carry the same observed dimensions and exact flat element encodings as [manual reads](opcua-runtime.md); exports and reopened journals retain and validate the complete envelope. Declared type/rank metadata is observed at subscription creation; it is not a promise that the server's declarations remain unchanged. Identity reads before services recheck namespace/server tables and server start time. Protected credential admission is rechecked for each service.

Publish envelopes retain original sequence number, publish time, available retransmission sequence numbers, MoreNotifications, submitted acknowledgements and original per-acknowledgement status. DataValue overflow information bits remain separate from sequence gaps. Keepalives advertise the next sequence number and contribute no sample. StatusChangeNotification codes are retained and terminate acquisition. Event subscriptions and other extension-object forms refuse; there is no silent conversion to polling.

Duplicate notification messages retain the original received envelope with `duplicate=true`; their notifications do not increase the unique notification count. Sequence gaps retain the original inclusive range and observed missing count even when a later Republish recovers it. The missing count describes detected gaps, not final unrecovered sample loss. Retransmissions have their own original sequence/time/data envelope. Bad_MessageNotAvailable remains a failure event with its requested sequence and exact status. The counter excludes sequence zero and wraps from 4294967295 to 1.

At most the requested total number of missing messages is republished. With recovery disabled, service failure ends acquisition. Opt-in recovery only reactivates the original session after a transport error or timeout; it cannot transfer or recreate a subscription. Messages are acknowledged only after their complete local envelope has been committed. An acknowledgement is not a guarantee that every intermediate PLC value was sampled or retained by the server.

## Bounds and lifecycle

The request admits 1–64 distinct nodes, publishing intervals 100–1,000 ms, sampling intervals 0–60,000 ms, queue sizes 1–1,024, keepalive counts 1–10, lifetime counts from three times keepalive through 1,000, duration 1–3,600 seconds, 1–100,000 notifications, 1–64 MiB and 0–64 Republish attempts (default 16). One original notification per Publish is requested, avoiding silent truncation at the exact notification limit. Server revisions are retained rather than represented as requested values. Each service uses the existing ten-second session deadline. Stop is observed between calls and the deletion attempt has its own bounded budget; elapsed duration can include the final service and cleanup.

Four workers and one acquisition lease per session are admitted. Subscriptions, polling captures, manual reads/browse and explicit disconnect share that lease. Status remains a local observation. Existing polling requests use the same reservation and release it even after publication failure. No second acquisition can race the original session's cleanup.

The separate `runtime-ua-subscriptions` journal reuses the existing capture journal's immutable hash-linked segments, atomic committed-head replacement, path checks, reservation bounds and exact streaming exports. Limits remain 64 retained captures and 512 MiB with no automatic eviction. Canonical request/event validation and a chronological reconstruction independently verify item identities, duplicate/sequence evidence, per-segment counters and monotonic acquisition elapsed time. Source wall-clock changes remain possible and are not rewritten into monotonic source timestamps.

A crash retains only the committed prefix and unknown uncommitted loss. A partial publication may leave a tail, which is never adopted during reopen. A new Host reports `interrupted` and performs no network work for status/page/export/repeated start/stop. The original remote session and subscription remain unconfirmed until independent timeout/cleanup or explicit local acknowledgement; acknowledgement itself is not cleanup proof.

The profile does not establish independent PLC/program/address association, complete physical sampling, scan synchronization, native TIA behavior or physical server/CPU compatibility. Exact local hashes establish consistency, not remote authenticity beyond the selected encrypted session's certificate pin.

Contract: [opcua-runtime-subscription.v1](../../contracts/opcua-runtime-subscription.v1.schema.json). Qualification: [21 October 2026](../verification/opcua-subscriptions-20261021.md). Protocol references: [OPC Foundation subscription behavior](https://reference.opcfoundation.org/specs/OPC-10000-4/5.14.1.1), [Publish](https://reference.opcfoundation.org/Core/Part4/v105/docs/5.14.5) and [Republish](https://reference.opcfoundation.org/Core/Part4/v105/docs/5.14.6). Production uses the already pinned OPC Foundation .NET Standard 1.5.378.182 package.

## Recovery evidence

Each `reconnect` event contains `reconnect: {attempt, state, errorCode}`. Attempts
are numbered across the entire capture. `starting` and its `succeeded`/`failed`
result are committed separately, so a crash can retain an unfinished attempt as
uncertain evidence. An interruption is not a known count of lost notifications.

After reactivation, Republish starts at the next expected sequence. Each recovered
message retains its original envelope and a detected one-message gap; a
Bad_MessageNotAvailable response ends this drain. Only then does normal Publish
continue. These calls consume the original `maximumRepublish` budget. Exhausting
that budget ends acquisition with `UaReconnectRepublishLimit`, rather than silently
skipping recovery. A zero Republish budget therefore permits session reactivation
but cannot continue the subscription. Notification limits and server status changes
can also end the drain. No values or missing source times are interpolated.

The chronological reader rejects impossible attempt order, data after failed
recovery, Publish before the recovery drain completes, and acknowledgements of
uncommitted messages. JSON and CSV retain the full recovery events. Diagnostic
observations count the interruption as a gap and preserve the original evidence.
