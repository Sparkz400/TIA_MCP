# Owned Web API sessions

The initial RT-02 workflow connects, browses, reads, inspects and closes a Siemens Web API session through six MCP tools. It requires no local TIA or PLCSIM installation. A real CPU still needs an enabled and configured Web API, suitable permissions and reachable HTTPS. The implementation is **partial and physically unqualified**; protocol-fake success does not establish CPU identity, downloaded-program match or sensor quality.

## Prepare the credential

Obtain the selected controller's leaf-certificate SHA-256 independently. `runtime_web_credential_binding` accepts a canonical HTTPS origin, lowercase certificate digest, public username and an explicit mode (`local`, `static`, `disabled`, `umc`). It returns the public binding for the existing local credential CLI. The binding includes endpoint, certificate, mode and subject; it cannot be silently reused with another target.

Save that public binding and run `TiaMcp.Host.exe --credential-store create --binding binding.json`. Enter the password **as a JSON string**, including its quotes; `""` represents an empty password. Alternatively supply those JSON bytes through the CLI's existing `--stdin` input. Never pass a password or token as a tool argument, command-line argument or environment variable. The JSON password input is bounded to 32 KiB. Keep the returned public credential reference; the vault stores the secret with Windows current-user DPAPI. A named vault uses the CLI's existing `--vault-name` option.

## Connect, select, read, close

1. Call `runtime_web_connect` with a fresh 32-character lowercase hexadecimal `attemptId` and `login` containing `endpoint`, `peerCertificateSha256`, `user`, `mode`, `vaultName` (`default` or a named vault), and the public `credential` reference. Retain the attempt ID before sending. Reusing it observes the same attempt and never repeats login while its record is retained (see the record bound below).
2. `runtime_web_browse` with that attempt lists immediate children. To select a value, supply the exact quoted symbol and `children: false`; the Host fetches its metadata and returns a local `selectionId`. Display names are not automatically converted into qualified symbols.
3. `runtime_web_read` accepts the attempt and selection IDs, with `representation: simple` or `raw`. Before reading it checks the protected credential, advertised session identity, current permissions and exact symbol metadata again. An error has no previous good value. Successful JSON retains 64-bit numbers; consumers must use a lossless JSON reader if their ordinary numeric type cannot represent them.
4. `runtime_web_status` reads retained local state without network activity. A connected state is a local observation, not a new authentication probe. `runtime_web_disconnect` invalidates local selections and sends one logout for this Host's owned token.

5. `runtime_web_diagnostics` reads one CPU diagnostic `kind` in a connected session (see [CPU diagnostics](#cpu-diagnostics)).

Read results retain host UTC request/receive times and monotonic elapsed duration. Source/server timestamps remain absent; no scan synchronization, atomic multi-signal sampling, device/program verification or native qualification is claimed. The [protocol contract](physical-runtime-contracts.md) describes the result format.

## Trust, ownership and bounds

TLS uses the explicitly supplied leaf pin as its trust anchor, plus hostname, validity and server-usage checks. This deliberately supports independently verified self-signed controller certificates without installing anything into the OS trust store. It is not automatic certificate discovery, trust-on-first-use, a CA-chain validation claim or revocation checking. Redirects, proxies and cookies are disabled. Requests close their connections so stale pooled connections are not reused and the selected certificate is checked on each connection. Each request has a ten-second deadline, a request bound of the smaller of 64 KiB and the observed endpoint limit, and a 256 KiB response bound; there is no automatic request/login retry. Operations serialize within the Host, and an exclusive local owner lock prevents two Hosts using the same runtime journal root.

Method discovery also has a 64 KiB serialized-name budget so retained status remains readable within its 128 KiB bound. There are at most four live sessions, 256 selections per session and 1,024 retained attempt records in one artifact root. When a new attempt finds 1,024 records, the Host deletes the oldest resolved ones (`closed`, `notConnected`, `abandonedRemoteUnknown`, by last write) to make room; unresolved records are never removed, and 1,024 of them refuse new attempts with `RuntimeSessionCapacity`. A retained attempt ID still observes its outcome; a deleted one counts as new. These are client limits, not Siemens quantity guarantees. The client rejects endpoints missing its required methods. Advertised optional profile methods are queried; an absent method remains explicitly unobserved. The client never derives a firmware version or CPU qualification from an API version number.

Tokens remain in memory and are not included in public results, journal records or credential references. Returned result data containing the current token is rejected. Remote error text is discarded; fixed error codes cross the credential-consumer boundary. Password/request buffers are cleared where owned, but managed JSON/string allocations are not a secure-heap erasure guarantee. Current credential validity is checked on each browse/read; retirement blocks further reads while still permitting owned-session cleanup.

## Discovery and session profiles

New observations use `runtime-web-session.v2`; existing v1 records reopen in their original format without network calls or an invented profile. The v2 `profile` appears after discovery starts and retains bounded typed observations:

- `Api.Version`: the positive numeric result is limited to 64 JSON characters and preserved as JSON, without conversion into a firmware version or a major/minor tuple.
- `Api.GetQuantityStructures`: only the V21 table/Siemens client `webapi_max_*` field profile is accepted. Fields must be positive integers. The contradictory example names without `max` are not silently treated as aliases; those responses refuse before login with `RuntimeQuantityProfileUnsupported`. `quantityProfile` identifies a parser contract, not a qualified CPU. Host concurrency stays serialized and local endpoint ownership permits only one unresolved session, regardless of larger advertised limits.
- `Api.GetAuthenticationMode`: the explicit chosen mode must be advertised when this method is available. The documented local `Anonymous` exception for a CPU advertising UMC remains possible. No mode fallback, SSO flow or automatic downgrade occurs.
- `Api.GetSessionInfo`: when advertised, the username and returned mode must match the selected login after authentication and before each browse/read operation. Mode omission is accepted only for `Anonymous`/`Everybody`. A failed recheck stops further browsing/reading and retains an unknown session outcome; only owned cleanup or explicit local abandonment remains possible.
- Login/session idle policy and password-expiration timestamp/warning are retained when supplied. `has_no_permissions` prevents a contradictory permission list from authorizing a value read. Missing fields remain unknown; client clock comparisons do not certify server password expiry.

Profile methods are optional for compatibility with older advertised method sets. Their absence does not establish identity, limits or mode support. Malformed/denied advertised profile responses do not silently fall back to a weaker result. Retained status remains an observation, not a fresh network probe. A session username match is not proof of CPU identity or downloaded-program match.

## CPU diagnostics

`runtime_web_diagnostics` takes the attempt ID and one `kind`. Each kind sends exactly one documented read-only Siemens method:

| `kind` | Method | Inputs | Checked result shape |
| --- | --- | --- | --- |
| `languages` | `Project.ReadLanguages` | none | `languages`: at most 64 objects with a `language` tag |
| `alarms` | `Alarms.Browse` | `language`, `count` 1..500 | `entries`: at most `count` objects |
| `diagnosticBuffer` | `DiagnosticBuffer.Browse` | `language`, `count` 1..500 | `entries`: at most `count` objects |
| `operatingMode` | `Plc.ReadOperatingMode` | none | a string such as `run` or `stop` |
| `modeSelector` | `Plc.ReadModeSelectorState` | none | `mode_selector` string |
| `systemTime` | `Plc.ReadSystemTime` | none | `timestamp` string |
| `timeSettings` | `Plc.ReadTimeSettings` | none | `current_offset` and `utc_offset` strings |
| `projectInformation` | `Project.ReadInformation` | none | an object |

Take the `language` tag from a `languages` read, for example `en-US`. The Host checks the credential and session identity first, as for `runtime_web_read`. If `Api.Browse` did not advertise the method, the result is `unsupported` and nothing is sent. Provider errors keep their code (`permissionDenied` for code 2) and never return remote text.

The result keeps the complete bounded JSON in `value`, including vendor fields the Host does not interpret, and the host request/receive times. Alarm and diagnostic-buffer results also report `entries` and `truncated`. `truncated` compares the entries returned with the CPU's `count_current`; it is null when the CPU does not report a total. CPU times stay as the CPU's text and are not reparsed. Use the host times to bound the latency of a clock comparison.

The Host does not acknowledge alarms, apply filters or alarm IDs, set the clock or change the operating mode. It does not send the R/H `redundancy_id`, so redundant systems read their default CPU only. `Plc.ReadMemoryInformation` and `Plc.ReadRuntimeInformation` are not exposed. A snapshot is not lossless alarm or event history, and project information does not prove which program is loaded. Request and result shapes come from Siemens documentation and the [Siemens Web API client](https://github.com/siemens/simatic-s7-webserver-api); only protocol-fake tests cover them, no CPU.

## Owned PLCSIM writes and RUN/STOP

Writes and mode changes exist only for an owned PLCSIM Advanced instance, never for a physical PLC. First call `runtime_plcsim_bind` with the instance ID, name and the `creationId` it was created or adopted with. The simulator worker reports the instance name, storage path, the SHA-256 of its ownership marker, its usable controller IPv4 addresses and its admitted `communicationInterface` (`Softbus` or `TCPIP`, see [simulation](../guides/simulation.md)), and the Host retains them as a content-addressed binding under `runtime-plcsim-bindings/`. With a current `downloadTicket` the Host also runs `plc_online_compare` against the same instance and records its fingerprint hash, when Siemens exposes one.

`runtime_web_write` and `runtime_web_operating_mode` take that binding's `bindingSha256`. Before anything is sent, the binding must record `TCPIP`, the session endpoint must be a literal controller address of the binding, and the simulator worker must still see the same owned instance. Otherwise the call fails with `PlcsimEndpointUnreachable` (a Softbus binding, or one retained before the interface was recorded: no network endpoint reaches it), `RuntimeTargetNotOwnedPlcsim` or `PlcsimTargetBindingStale`; a mode switch during the window fails with `TcpIpModeRequired` from the simulator worker. Each call also needs its acknowledgement flag and the matching permission (`write_value` or `change_operating_mode`). If `Api.Browse` did not advertise the method, the result is `unsupported` and nothing is sent.

| Tool | Method | Precondition | Readback |
| --- | --- | --- | --- |
| `runtime_web_write` | `PlcProgram.Write`, simple mode, 1..16 selections | each `expected` equals the current `PlcProgram.Read` value | one `PlcProgram.Read` per written value |
| `runtime_web_operating_mode` | `Plc.RequestChangeOperatingMode`, `run` or `stop` | `expectedMode` equals the current `Plc.ReadOperatingMode` | `Plc.ReadOperatingMode` for about 5 s |

A failed precondition returns `preconditionMismatch` and nothing is written. Once a RUN/STOP request may have gone out, a lost response or a failed or cancelled mode readback returns `state: unknown` with `dispatched: true`, the `dispatch` status and `errorCode`, never a plain failure. Writes go out once each, in order. The sequence stops at the first provider error, unknown outcome or readback mismatch. The program scan can overwrite an assigned value. A lost response is reported and never retried. `Alarms.Acknowledge` is not implemented: its parameters and the alarm acknowledgement state are not part of the verified protocol. PLCSIM Advanced serves its web server only with TCP/IP communication, and this adapter never reconfigures an instance.

## Interruption and cleanup

The Host writes intent before login. A lost or malformed login response remains `remoteSessionUnknown`; a different attempt ID cannot bypass that unresolved endpoint reservation. An authoritative login rejection is `notConnected`. Logout intent is likewise retained before dispatch, and a lost logout response is never automatically retried.

Graceful Host shutdown and idle expiry close owned sessions. The local idle limit is the smaller of 90 seconds and a supplied positive `runtime_timeout`; a one-second maintenance loop handles expiry after any in-flight bounded operation. Abrupt Host termination loses the in-memory token. A later Host reports `hostEndedRemoteUnknown`, retains the original attempt and refuses automatic login or cleanup claims. Existing inventory tokens are never adopted or logged out.

If the remote outcome cannot be established, `runtime_web_disconnect` with explicit `acknowledgeUnknown: true` releases the unresolved **local** reservation. It records `abandonedRemoteUnknown`, performs no network action and does not claim remote logout or expiry. A separately requested fresh attempt can then proceed. This option cannot abandon a currently connected live session; disconnect it normally. Only resolved records are deleted to make room (see the record bound above); a root full of unresolved records requires an explicit archival/recovery decision.

## Evidence and remaining work

The [session baseline](../verification/physical-runtime-sessions-20261017.md) and [profile verification record](../verification/physical-runtime-profiles-20261017.md) distinguishes focused tests from frozen aggregate/package validation. The loopback harness uses actual TLS, actual DPAPI and actual MCP Host processes, including a forced restart; it does not contact a physical PLC or change OS trust.

Remaining RT-02 work includes genuine firmware-specific profile transcripts, additional device/I&M observations, unresolved alternate quantity spellings and broader transport/compatibility stress cases. RT-03 capture/reopen/export, OPC UA, physical qualification of the [CPU diagnostics](#cpu-diagnostics), program association, physical write/control gates and the rest of D16 remain open. Only bound [owned PLCSIM](#owned-plcsim-writes-and-runstop) endpoints accept writes or RUN/STOP; nothing enables forcing or download.

## Sources

[Siemens V21 Api.Login](https://docs.tia.siemens.cloud/r/simatic_s7_1500_et_200mp_manual_collection_enus_21/comprehensive-information/cpu-function-manuals/web-server/api-application-programming-interface/web-api-basic-functions/api.login) defines explicit login modes, token handling and optional idle timeout. [Siemens GetAuthenticationMode](https://docs.tia.siemens.cloud/r/simatic_s7_1500_et_200mp_manual_collection_enus_21/comprehensive-information/cpu-function-manuals/web-server/api-application-programming-interface/web-api-basic-functions/api.getauthenticationmode?contentId=Z1bdTWI1m9yCcNsbJ5YSsQ) describes mode availability. The [Siemens-owned request handler](https://github.com/siemens/simatic-s7-webserver-api/blob/faac56e912c6bacbf620bf4169d61f5c8f4e00c7/src/Webserver.API/Services/RequestHandling/ApiHttpClientRequestHandler.cs) corroborates token-header use and the true-on-success logout response; its automatic relogin behavior is not adopted here. Context7 did not return a matching Web API library, so these primary sources were inspected directly.

[Siemens Api.Version](https://docs.tia.siemens.cloud/r/simatic_s7_1500_et_200mp_manual_collection_enus_21/comprehensive-information/cpu-function-manuals/web-server/api-application-programming-interface/web-api-basic-functions/api.version?contentId=3VAPA~me~D1a69_vCnqvsA) defines the numeric result. [GetSessionInfo](https://docs.tia.siemens.cloud/r/simatic_s7_1500_et_200mp_manual_collection_enus_21/comprehensive-information/cpu-function-manuals/web-server/api-application-programming-interface/web-api-basic-functions/api.getsessioninfo?contentId=giGourGr_puxCW48PR97NQ) defines username, optional mode and policy fields. The [quantity table and conflicting example](https://docs.tia.siemens.cloud/r/simatic_s7_1500_et_200mp_manual_collection_enus_21/comprehensive-information/cpu-function-manuals/web-server/api-application-programming-interface/web-api-basic-functions/api.getquantitystructures?contentId=G6LTb2KPJs8L7HApSl7JYg) are resolved only to the explicit table profile corroborated by the [Siemens client model](https://github.com/siemens/simatic-s7-webserver-api/blob/faac56e912c6bacbf620bf4169d61f5c8f4e00c7/src/Webserver.API/Models/ApiQuantityStructure.cs); no actual firmware response was available.

## Bounded acquisition

Use the [retained capture workflow](runtime-captures.md) to poll exact owned selections, reopen committed prefixes, export JSON/CSV and view results offline in Explorer. Capture stop sends no logout, but cancellation during session identity verification can invalidate that session and require explicit disconnect/reconnect. Full RT-03 and native qualification remain open.

## Connection lifetime

The Host enforces zero connection-pool lifetime in addition to sending `Connection: close`. Each RPC uses a fresh TCP connection; a peer ignoring the request header cannot cause local reuse. TLS resumption is also disabled because a resumed session can retain a prior certificate after server rotation. Each RPC therefore requires a full handshake and the selected pin/name/validity/EKU checks. This adds handshake overhead; native device timing and load remain unqualified. No credential or request retry is added. See [transport regression evidence](../verification/runtime-transport-20261017.md) and the [bound lifecycle verification](../verification/security-runtime-lifecycle-20261018.md).

Response-stream I/O failures retain the sanitized `RuntimeTransportFailure`
code and no value. An explicitly scheduled later poll is a new observation;
the missing response is never filled with a prior value or silently replayed.
