# Online device identification on PLCSIM — 2026-11-01

`device_online_browse` and `device_identify` passed natively against an owned PLCSIM Advanced V8 instance in TCP/IP
mode. Evidence: `local.device_inventory.online.20261101`. The [16 October record](device-inventory-20261016.md)
covered offline capture and protocol fixtures only; the prerequisites are listed under
[X10 online rows](hardware-families-20261029.md#x10-online-rows).

## Run

[`qualify_runtime_plcsim_tcpip.py`](../../scripts/qualify_runtime_plcsim_tcpip.py) `--mode device-inventory`, run
`rs_ad8a41e4` (`artifacts/x10s`) at `a6f76370`, Host and workers frozen at `b4f4785d` (`artifacts/x10c`), 58/58 steps;
the oracle [`verify_runtime_plcsim_tcpip.py`](../../scripts/verify_runtime_plcsim_tcpip.py) found no issue.

1. **Setup.** The mode's shared setup: an owned scratch CPU 1511-1 PN V4.1 at 192.168.0.1 with the web server and
   system pages on, the generated runtime user granted `read_diagnostics`, downloaded to a fresh owned instance, and
   the web certificate pinned from `certificate_inspect`.
2. **Trust.** The runner wrote the instance's self-signed web certificate (`CN=ScratchPLC/Webserver-2`) to the run
   directory and printed the import and removal commands. The operator imported it into `Cert:\CurrentUser\Root`,
   and the coordinator answered `trusted`. The runner only checked the store (`Test-Path`). After the run the
   certificate is no longer in the store.
3. **Session.** `Modules.Browse` without a session answered `Permission denied`. The runtime user logged in over a
   connection pinned to the certificate (`runtime_timeout` `PT30M`; `read_diagnostics`, `read_value`, `write_value`,
   `change_operating_mode`). The token reached only a fresh Host, as a `TIA_INVENTORY_<target>` record named by
   `sessionEnvironmentVariable`, and was logged out at the end. No retained file contains it.
4. **Snapshot.** That Host reopened the saved scratch in an owned TIA and captured `device_inventory` (9 rows). The
   snapshot has to come from the Host that calls `device_identify`.
5. **Browse.** `device_online_browse` walked the station node (hwid 32), the CPU (49), its PROFINET interface (64),
   both ports (65, 66) and the virtual communication interface (135). Each answer equals the runner's own pinned
   `Modules.Browse` of the same subtree, field by field.
6. **Identify.** `device_identify` read I&M0 for the CPU, the interface and both ports. Order number, serial number,
   hardware revision and software revision equal the pinned `Modules.ReadIdentificationMaintenance` reads. The CPU's
   configured MLFB `6ES7 511-1AL03-0AB0` and firmware V4.1 match its I&M0.

## Fixed on the way

- **HTTP 500 from PLCSIM.** Run `x10q`: PLCSIM's web server answered the tools' chunked
  `application/json; charset=utf-8` request with HTTP 500. `SiemensWebIdentity` now sends a sized HTTP/1.1 body with
  plain `application/json`, as `WebRuntimeClient` does (`b4f4785d`). The inventory tests pin that body contract.
- **CPU binding.** Run `x10r`: PLCSIM gives the station node the CPU's name and nests the CPU under its `children`,
  so the runner bound nothing to the CPU. It now walks nested children and binds the CPU row to the one
  cpu-subtype node of that name (`a6f76370`).

## Limits

- One PLCSIM CPU 1511-1 PN without modules; physical PLCs, ET 200 stations and other families are not exercised.
- The tools keep Windows TLS validation without a pin, so the operator trusts the instance's certificate for the run.
  A new provisioning makes a new certificate. A pinned route is not offered.
- Rows bind by exact unique names. On PLCSIM the interface and its ports answer the CPU's own I&M0, so module-level
  I&M0 of separate hardware is not shown.
