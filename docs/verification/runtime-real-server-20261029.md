# Runtime real-server runs — 29 October 2026

Tasks 4 and 5 of the [runtime real-server plan](../plans/runtime-real-server-plan.md): the Host's
`runtime_ua_*`, `runtime_web_*` and `runtime_capture_*` tools against the real OPC UA and web
servers of an owned PLCSIM Advanced V8 instance in TCP/IP mode, with recovery. Curated as
`local.runtime.plcsim-tcpip.20261029`.

**Result:** all five modes passed on one frozen build (`1181f33`), each with the independent
file-only oracle (`scripts/verify_runtime_plcsim_tcpip.py`) reporting no issues. The `normal`
run answered all 34 runtime tools without error.

## Setup (operator)

- PLCSIM Advanced V8, *Online Access* = *PLCSIM Virtual Eth. Adapter* (TCP/IP single adapter).
- The adapter (`Ethernet 2`) got the static address `192.168.0.10/24`, DHCP off; afterwards the
  operator restored DHCP and switched back to Softbus.
- The other sessions held all PLCSIM and TIA work during the window; the runner refuses a run
  whose preexisting TIA processes change.

## What each run does

Every mode starts from a fresh owned scratch (CPU 1511-1 PN, `192.168.0.1`) on the frozen build:
simulation support, the `RealServer` and `DiagGate` program, a role for the generated runtime user
with `OPCUAServerAccess`, `WebReadTags`, `WebWriteTags`, `WebAllowDiagnostics` and
`WebChangeOperatingMode`, the web server (`webserver_page_set`) and the OPC UA server
(`opcua_server_settings_set`, automatic client-certificate acceptance), then `sim_create` with
`TCPIP`, `simulation_download`, `simulation_provision_scratch_credential` over the TCP/IP route,
power on and RUN. The UA and web server certificate pins come from `certificate_inspect` after the
last compile; a certificate the compile replaced is used as the stale pin.

Login is the scratch's generated runtime user (maintainer decision, 29 October): the runner copies
its DPAPI password into a run-owned Host vault through the credential CLI's stdin and retires it at
the end. Guest access is not settable through V21 Openness and stays unused.

| Mode | Run | Hosts | What it proves |
| --- | --- | --- | --- |
| `normal` | `rs_90d1c29f` | 1 | `runtime_plcsim_bind`; stale-pin connects end `notConnected` for UA and web; UA browse, read (each value beside the PLCSIM API read of the same tag), `write` with `expected` and a refused stale `expected`, status, subscription and polling capture to their duration with page and export; web browse, read, diagnostics, status, write (PLCSIM reads the effect), capture with page, export and `runtime_capture_analyze`, `runtime_web_operating_mode` RUN→STOP→RUN confirmed by PLCSIM; disconnects |
| `server-restart` | `rs_19346c58` | 1 | STOP, power off and power on during a live UA and web session: the changed server start time invalidates the sessions, both captures record the downtime, nothing reconnects by itself |
| `host-loss-capture` | `rs_57801a7c` | 3 | the Host is killed while a UA subscription and a web capture run; a new Host adopts the instance (`sim_select`), reports the lost sessions as unknown, pages and exports the committed prefix, refuses an implicit resume and a new session before acknowledgement; after `disconnect` with acknowledgement a fresh session reads |
| `ua-write-loss` | `rs_8dfc756e` | 3 | the Host exits right after the UA Write service returned (`--runtime-qualification ua.write.afterSend`); a new Host reports the unknown outcome, refuses a retry, an explicit read decides and the PLCSIM API read confirms |
| `web-write-loss` | `rs_2e24a073` | 3 | the same for `PlcProgram.Write` (`web.write.afterSend`) |

Each Host and worker exits with a verified exit proof, the session stores hold only the attempts
the runner opened, and the owned instance is stopped, powered off and released.

## Defects found and fixed during the window

- The empty TCP/IP CPU has no IP yet, and TIA shows it by its MAC (`02-C0-A8-00-0A-00`, series
  `S7-1500 (PLCSIM)`); provisioning refused it (`b1ca505`).
- After the bootstrap TIA's TCP/IP discovery never showed the assigned address; the target is now
  decided by the MAC seen before the bootstrap while the instance reads back the address, and
  rechecked by MAC and TLS certificate series (`6c6701d`).
- PLCSIM's tag list was stale after a restart or download (`0961d95`).
- A TCP/IP `GoOnline` threw once before any callback, minutes after another owned CPU left the
  address; it is retried once, never on Softbus (`029c347`).
- A UA subscription and a web capture in one Host collided on the shared vault lock and the
  second was reported as a missing credential (runs `rs_abde1bb3`, `rs_db5b4b21`). Vault users now
  take turns in arrival order for up to 5 seconds; past that the call reports
  `UaCredentialStoreBusy` and the session stays connected (`1181f33`). Runs before `1181f33` are
  not curated.

## Limits

- Owned PLCSIM Advanced V8 on this machine only. A physical PLC (`runtime.physical_plc`) and
  another machine or network stay unqualified.
- One CPU type and firmware (1511-1 PN), one runtime user, the documented bootstrap address.
- `plc_online_compare`, `plc_station_upload` and the discovery baseline stay Softbus-only.
