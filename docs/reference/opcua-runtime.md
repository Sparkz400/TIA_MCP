# Owned OPC UA read-only sessions

The online client is separate from `TiaMcp.OpcUa`, which continues to parse and review offline NodeSet files. The runtime client needs no TIA installation. A configured OPC UA server must already be enabled, licensed where required, reachable, and configured to trust this client's public application certificate. No engineering configuration, physical-device qualification or downloaded-program identity is inferred.

The implementation pins the maintained OPC Foundation .NET Standard **1.5.378.182** client package, including native .NET 10 assets. It deliberately supports a narrower profile than the SDK: explicit `opc.tcp` URL, exact server application URI, supplied leaf-certificate SHA256, `Basic256Sha256`, `SignAndEncrypt`, and either anonymous or protected username authentication. The supplied pin establishes trust in that exact leaf; validity, RSA key size/usage, server authentication usage, application URI, hostname and SDK signature checks remain mandatory. No other policy or endpoint is selected automatically. The SDK validates agreement between the discovery certificate and authenticated session certificate. [Dependency provenance and license notices](../../third-party/opcua-runtime/README.md).

1. Choose an ephemeral identity (the default), or configure a [protected persistent identity](#protected-persistent-client-identity) before starting the Host. Call `runtime_ua_client_identity`; its DER is the public certificate for explicit server trust provisioning. The response includes the storage mode, validity dates and `current`, `expired` or `notYetValid` state. Private keys are never returned. In ephemeral mode, restarting the Host creates a new identity and requires new server trust provisioning.
2. For username authentication, obtain `runtime_ua_credential_binding` with the exact endpoint, application URI, certificate pin, username and vault name. Store a JSON-string password using the existing local [protected-credential CLI](protected-credentials.md). Supply the returned reference to `runtime_ua_connect`; never pass passwords through tool arguments. The vault consumer remains held while credentials are used. Each later browse/read rechecks the protected reference before network work.
3. Call `runtime_ua_connect` with a new 32-character lowercase hexadecimal attempt ID. Repeating that ID observes the retained outcome; it does not create another session.
4. Browse immediate hierarchical references with `runtime_ua_browse` and read explicit nodes with `runtime_ua_read`. Node identity uses the exact namespace URI plus a canonical numeric, string, GUID or base64 opaque identifier. Namespace indexes are session-local and never accepted as durable identities.
5. Use `runtime_ua_status` for local retained state and `runtime_ua_disconnect` for bounded cleanup.

Acquisition and reads recheck the namespace table, server URI table and server start time. Changed observations invalidate the session. No implicit reconnect, node registration, history read, arbitrary service invocation, method Call, forcing or download surface is exposed. The only write is `runtime_ua_write`, for an owned PLCSIM endpoint bound with `runtime_plcsim_bind` (see [owned PLCSIM writes](physical-runtime-sessions.md#owned-plcsim-writes-and-runstop)). It writes 1..16 scalar values only when each `expected` value equals the current `runtime_ua_read` encoding and value, sends one Write call, then reads every node back. The stack performs the secure-channel and session services needed for authenticated reads. The separate [subscription workflow](opcua-runtime-subscriptions.md) explicitly creates bounded monitored-item acquisition; connecting alone does not start it.

Values retain the decoded built-in type, declared DataType identity, ValueRank and ArrayDimensions, raw 32-bit status, Good/Uncertain/Bad classification, original nullable source/server timestamps and both picosecond fields. Integers use exact decimal strings, including Int64 and UInt64; floating values use round-trip strings. A missing timestamp remains null. A decoded scalar DateTime remains a date value, including its minimum decoded value; it is not treated as a missing measurement timestamp.

Bad item values are suppressed while status/type/time metadata remains available. Uncertain values stay explicitly uncertain. Arrays and matrices of supported scalar types use `encoding: "array"` with `{dimensions, elementEncoding, elements}`. Elements are flat in row-major order (last index varies fastest), retaining the scalar encodings above. Observed `dimensions` are separate from the original declared `arrayDimensions` maxima; zero declared dimensions mean unknown maxima. Empty arrays retain an observed zero dimension and an empty element list. A typed null array has null dimensions and elements; its shape is unknown. When the SDK decodes a null Variant without type information, `encoding: "null"` and the original declared rank are retained; no element type or observed shape is inferred. Null String/ByteString elements remain null. A scalar ByteString stays base64; a Byte array contains decimal byte strings, and a ByteString array contains base64 strings.

At most 4,096 elements and 32 dimensions are admitted, with each observed dimension bounded to 4,096. Declared rank/maximum shape must admit the observed shape. Malformed shape, unsupported element types (including structures/variants) or non-finite numeric elements refuse the entire value without publishing a partial array. The original per-value status and timestamps apply to the whole array; no per-element quality or time is invented. Extension objects and other unsupported forms remain explicit unsupported outcomes. Per-item missing-node and access errors do not erase successful neighboring items. Source timestamps and Good quality describe the UA response, not independent sensor or machine-safety proof. A known credential echoed in a returned payload is rejected.

Bounds are fixed: four live owned clients per artifact store, 1,024 retained attempt records, 64 nodes per read, 256 references and 16 pages per browse, one MiB stack message/output limits, 16,384-character strings, 65,536-byte byte strings and 4,096 array elements. Operations, including gate admission, have a ten-second linked deadline. Continuation release has a separate ten-second cleanup budget. Client idle cleanup starts after two minutes without a successful identity check; disposal closes only captured owned clients. There is no system-wide exhaustion or disk-full action.

Retained records are under `runtime-ua` with a single owner lock. No private key, password, UA authentication token or reusable session handle is recorded. An interrupted connect/read or unconfirmed close remains `remoteSessionUnknown`; an actually different Host observes `hostEndedRemoteUnknown`. An unresolved endpoint reservation blocks fresh IDs in the same artifact store. Explicit `acknowledgeUnknown=true` records `abandonedRemoteUnknown` and releases only the local reservation; it does not claim remote logout. Browse cleanup failure is retained separately from the original operation error where both occurred. Record publication failure cannot skip closing other captured live transports or releasing the original owner lock.

The [contract](../../contracts/opcua-runtime.v1.schema.json), [session qualification](../verification/opcua-runtime-wave4-20261020.md) and [array/matrix checks](../verification/opcua-array-values-20261025.md) describe the session profile and actual local evidence. Siemens CPU model/firmware, security configuration, real trust provisioning and runtime license compatibility remain physical qualification prerequisites. The [bounded polling capture workflow](opcua-runtime-capture.md) reuses these exact secure reads and retains their quality/status/timestamps. [Bounded subscriptions](opcua-runtime-subscriptions.md) have a separate retained contract and qualification. Captures can explicitly opt into [bounded reconnect](#controlled-acquisition-reconnect); only a subscription capture may open a replacement session, to transfer its subscription after the server reports the session gone. Mutation workflows remain unsupported.

An active polling capture or subscription exclusively reserves its owned session. Manual read, browse, disconnect and another acquisition refuse with `UaSessionAcquisitionBusy` until it stops and releases that reservation. Retained status remains available. Stop the original acquisition first, then explicitly disconnect; stopping acquisition and closing the session have separate outcomes.

## Protected persistent client identity

This Windows-only option preserves the application URI and certificate across Host restarts. Create it explicitly with the Host executable, then add the name to the Host's MCP launch arguments:

```powershell
./TiaMcp.Host.exe --ua-identity-store create --name default
./TiaMcp.Host.exe --ua-identity-store export --name default --output ./client-public.der
./TiaMcp.Host.exe --no-native-workers --artifacts C:/TiaMcpData --ua-client-identity default
```

Use `default` or a distinct 32-character lowercase hexadecimal name. The public DER file must be new; export refuses to overwrite an existing file. Provision this public certificate in the selected server's trust list using that server's normal administration workflow. The CLI never changes server trust. Without `--ua-client-identity`, existing ephemeral behavior remains unchanged.

The packaged launcher and MCP configuration generator accept `-UaClientIdentity <name>` and forward it to the Host. Create the identity under the Windows account on the machine that will execute the Host, including for SSH/session-relay launch. See [deployment](../guides/deployment.md).

The identity lives under `%LOCALAPPDATA%/TiaMcpOpcUaIdentities/<name>`, separately from portable artifacts and login-password vaults. It reuses the protected credential store's current-user-only ACL checks, path/hard-link refusal, bounded strict JSON and atomic file replacement. Windows DPAPI CurrentUser protects the PFX, bound to the exact directory, owner, vault identity, application URI and public certificate. Decrypted PFX buffers are cleared; key import uses .NET's `EphemeralKeySet`, so the imported private key is not separately written to the OS key store. There is no private-key import/export command. This protection does not isolate the key from other programs running as the same Windows user.

One Host exclusively owns each named identity for its lifetime. A second Host, CLI inspection/export or rotation reports `CredentialStoreBusy` until that owner exits. Stop the owning Host before administering the identity; while it runs, `runtime_ua_client_identity` supplies public status. Normal exit and process loss release the OS file lock. A copied or partially deleted store is not automatically repaired or rebound to another path, user or machine. Keep the Windows profile and original protected store for recovery; a portable artifact bundle does not contain the private key.

Inspect or rotate explicitly while the identity is idle:

```powershell
$identity = ./TiaMcp.Host.exe --ua-identity-store inspect --name default | ConvertFrom-Json
./TiaMcp.Host.exe --ua-identity-store rotate --name default --expected-sha256 $identity.certificateSha256
./TiaMcp.Host.exe --ua-identity-store export --name default --output ./client-public-rotated.der
```

Certificates use RSA 2048 and a twelve-month lifetime. Rotation keeps the application URI but replaces the key and certificate. It requires the exact current fingerprint, including when the old certificate has expired. A stale fingerprint refuses, so retrying after a lost rotation reply cannot rotate twice; inspect to discover the current result. Atomic publication failure preserves the old complete record. Missing, damaged, unreadable or insecure stores refuse without generating replacements. Choose a fresh name and explicitly provision its public certificate if the original identity cannot be recovered. The CLI cannot recover damaged private-key bytes or revoke a certificate on a server.

An expired or not-yet-valid identity remains inspectable but refuses new connections with `UaClientIdentityTimeInvalid`. Rotation requires new explicit server trust provisioning. Reusing the identity never restores old session tokens, reconnects, resumes polling/subscriptions or clears unresolved endpoint reservations. Each Host still has a new ownership ID and the existing uncertain-outcome rules apply. Encrypted loopback validation establishes software behavior only; real-device certificate acceptance remains unqualified. See [implementation evidence](../verification/opcua-persistent-identity-20261025.md).

## Controlled acquisition reconnect

Polling and subscription start requests accept `maximumReconnectAttempts` from 0
through 3. The default is 0, preserving fail-on-interruption behavior. A positive
value permits that many attempts across the entire live capture, with a 500 ms
cancellable delay before each attempt and the existing ten-second service budget.
Recovery consumes the original duration, sample/notification and storage budgets.

Only a transport interruption or service timeout can initiate recovery. The same
Host must still own the original session and its acquisition lease. Each attempt
rechecks protected credential admission and client/server certificate validity,
reactivates the original session on an encrypted channel, and verifies its original
session ID, namespace and server-URI tables, and server start time. Identity changes,
expired/lost sessions, retired credentials, authentication failures and invalid
responses stop recovery. There is no trust update or release of an unresolved
endpoint reservation. One exception applies to subscription captures: if
reactivation fails because the server reports the session gone, the client opens
one replacement session with the same endpoint, certificates and identity and calls
TransferSubscriptions; a refused transfer re-creates the subscription. See
[session loss and transfer](opcua-runtime-subscriptions.md#session-loss-and-transfer).
Polling captures never open a replacement session.

The journal commits a `starting` event before network recovery, followed by
`succeeded` or `failed` with a bounded error code. Their original monotonic elapsed
and wall-clock observations delimit an interruption with unknown remote data loss.
A successful reconnect does not establish complete sampling. Polling retains its
failed read and skipped slots, then continues at the next scheduled cycle.
Subscriptions retain their original subscription/item identities and drain bounded
Republish responses before sending Publish again; retransmissions, detected gaps,
duplicates, unavailable messages and overflow remain separate evidence.

Repeated start, status, page, export and stop after a Host restart remain local
observations. They never reactivate a session or restart acquisition, including
when the original request opted into recovery. Stopping during recovery cancels
further attempts; uncertain remote state remains reserved. Explicit session cleanup
continues to report its own outcome.

This uses the pinned SDK's session reactivation, following the
[OPC Foundation reconnect sequence](https://reference.opcfoundation.org/specs/OPC-10000-4/6.7).
No background SDK reconnect handler, ResendData method or write service is enabled. See [local implementation evidence](../verification/opcua-controlled-reconnect-20261025.md).
