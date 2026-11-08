# Syslog through the Web API on PLCSIM TCP/IP (8 November 2026)

Evidence: `local.runtime.plcsim-tcpip.syslog.20261108`, `scripts/qualify_runtime_plcsim_tcpip.py --mode normal` on an owned PLCSIM Advanced V8
instance in TCP/IP mode (scratch CPU 1511-1 PN V4.1 at 192.168.0.1, firmware R41.24.7).

- The complete normal-mode flow and its independent verification passed again.
- `runtime_web_diagnostics` kind `syslog` with `count` 5 returned status `read`, five raw RFC 5424 entries (the web
  server login of the run's own user, OPC UA server events) and the `count_total`/`count_lost` counters.
- `count` 21 was refused with `RuntimeDiagnosticSelectionInvalid` before any request.

The first run that day answered `permissionDenied` (provider error 2): `Syslog.Browse` needs the device function right
`WebReadSyslog` ("Read syslog buffer of the CPU"), which `WebAllowDiagnostics` does not include. The runner now asks
`umac_role_set` for an unknown right, whose refusal lists every available device right, and grants the syslog one with
the others.

Not covered: a physical CPU, the 20-entry bound with lost entries, and R/H systems (`redundancy_id`).
