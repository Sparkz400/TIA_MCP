# Runtime real-server feasibility gate — 28 October 2026

Task 0 of the [runtime real-server plan](../plans/runtime-real-server-plan.md). It checks the plan's
premise: a TCP/IP-mode PLCSIM Advanced V8 instance serves a CPU's OPC UA and web endpoints that
the existing Host runtime client can reach. This is a gate, not curated evidence; it closes no
matrix cell.

**Result:** the premise holds. OPC UA and the Web API both pass end to end through the Host's
runtime tools. The web login was `Anonymous` because the CPU's web authentication is disabled;
a named user with a password is still unexercised.

## Setup (operator)

- PLCSIM Advanced V8 Upd1, *Online Access* = *TCP/IP Single Adapter*, *TCP/IP communication
  with* = the Siemens PLCSIM Virtual Ethernet Adapter (`Ethernet 2`). Selecting the physical
  Realtek adapter instead bridges the instance onto the LAN, and TIA's download finds no device.
- `Ethernet 2` got the static address `192.168.0.10/24`, no gateway. No other interface routes
  `192.168.0.0/24` (LAN `192.168.178.0/24`, VirtualBox `192.168.56.0/24`).
- A throwaway CPU 1511-1 PN V4.1 project (`PLC_1`, `192.168.0.1`) with the OPC UA server
  (guest access, *automatically accept client certificates*) and the web server, downloaded
  from the TIA UI to a manually created instance and set to RUN. The download first refused
  with "'Main [OB1]' cannot be simulated" until the project's *Support simulation during block
  compilation* was set and the software was rebuilt.
- While PLCSIM was in TCP/IP mode, the other sessions held all PLCSIM work.

## OPC UA

Host: frozen build `D:\wp\b53` (source `74bac9ba1f394b2ad620c22bf80fef60cf19cacb`), started with
`--no-native-workers` and an ephemeral client identity.

- **Pin source.** An unauthenticated `GetEndpoints` (SecurityPolicy None) returned two endpoints
  for `urn:SIMATIC:PLC_1`: None/None and Basic256Sha256/SignAndEncrypt, both anonymous-only.
  The server leaf SHA256 is `d118ae4808ed26f213d5d2eda483d008f42642f8ecebc9daee24e7284c462eba`
  (SHA1 `de360446edd7eb1bc418e1cabdb52aaf8c589d27`). The pin was taken from that discovery, not
  from TIA's certificate manager as the plan proposed; Task 4 takes it from `certificate_inspect`.
- **`runtime_ua_connect`** with endpoint `opc.tcp://192.168.0.1:4840/`: `connected`,
  `peerVerified`, Basic256Sha256 SignAndEncrypt. The SDK's certificate, application-URI and
  hostname checks passed against the Siemens leaf.
- **`runtime_ua_browse`** of `ObjectsFolder` (i=85): `Server`, `DeviceSet`, `PLC_1`, not truncated.
- **`runtime_ua_read`**: ServerStatus.State (i=2259) `0` (Running), CurrentTime (i=2258) live,
  BuildInfo.ProductName (i=2261) `SIMATIC S7-1500 OPC UA`, all Good.
- **`runtime_ua_disconnect`** then `runtime_ua_status`: `closed`. The Host exited with code 0.

**Finding: the endpoint must carry a trailing slash.** The URL as the server advertises it,
`opc.tcp://192.168.0.1:4840`, is refused before any network work with
`UaEndpointSelectionInvalid`, because `OpcUaRuntimeSessionStore.Validate` requires
`Endpoint == Uri.AbsoluteUri`, which appends `/`. The canonical form connects. Either the
validator should canonicalize, or the refusal and the reference should name the expected form.

## Web server

- `https://192.168.0.1` answers with TLS 1.3. Leaf `CN=PLC-1/Webserver-3`, SAN IP and DNS
  `192.168.0.1`, valid 2026-10-28 to 2037-10-27, SHA256
  `0bdf8bc5a4306edaa9e9aad676f645ba7e797d7805681586483d72ce2496278b`.
- Unauthenticated `Api.Ping` and `Api.Version` succeed; the Web API version is 6.
  `Api.GetAuthenticationMode` advertises only `disabled`: the project configures no web users.
- **Credential.** `runtime_web_credential_binding` for `https://192.168.0.1`, that pin, user
  `Anonymous` and mode `disabled`. The JSON string `""` (no secret) went into a throwaway vault
  through `--credential-store create --stdin --vault-name <32-hex>` and was retired afterwards.
- **`runtime_web_connect`**: `connected`, `peerVerified`, profile API 6 /
  `siemens-v21-table-max` (131072-byte requests, 4 parallel, 200 sessions), session user
  `Anonymous`, reads `permittedAtObservation`.
- **`runtime_web_diagnostics`**: `operatingMode` `run` (`Plc.ReadOperatingMode`);
  `projectInformation` project `Scratch`, TIA Portal `V21.0.0.0`.
- **`runtime_web_browse`** of the root: `Tag_1` (`%M1.0`) and `Tag_2` (`%M2.0`), both Bool,
  immediate children only.
- **`runtime_web_disconnect`** then `runtime_web_status`: `closed`. The Host exited with code 0.

A named-user login (`local` or `umc` mode, with a real password through the credential prompt)
needs a project with web users; Task 4 provisions its own scratch program and covers it there.

## Next

1. Switch *Online Access* back to Softbus and revert the adapter address (plan Task 0, Step 6).
2. Tasks 1–2 (TCP/IP admission in the simulator worker, bindings for TCP/IP endpoints) can start.
