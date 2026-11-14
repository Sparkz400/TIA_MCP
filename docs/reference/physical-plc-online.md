# Online access to real PLCs

**Status: implemented, not qualified.** No physical PLC was available when these tools were written, so every result
must be checked in the TIA Portal. Use them only on a bench or test rig you own, never on a running plant.

The tools are hidden unless the Host is started with `--physical-plc-online`; the Host then passes the same flag to the
V21 worker, which refuses the operations without it (`PhysicalOnlineDisabled`). They are in the `physical`
[tool group](mcp-surface.md#tool-groups---toolsets) and run only in a worker-owned instance (`tia_start`); a user-owned
TIA is never taken online.

| Tool | What it does | Writes to the PLC |
| --- | --- | --- |
| `plc_physical_targets` | Lists connection modes, PG/PC interfaces (with `isPlcsim`), target interfaces and the IPv4 addresses the project configures for the CPU | No; nothing goes online |
| `plc_physical_online_compare` | Goes online, runs `PlcSoftware.CompareToOnline`, goes offline | No |
| `plc_physical_station_upload` | Uploads the station into a new project under the uploads artifacts, in an empty owned instance | No |
| `plc_physical_download` | Downloads software (changes or all; optionally hardware) | **Yes** |

## Route

Every call names its route: `pcInterface` and `pcInterfaceNumber` and, when there are several, `targetInterface`, as
`plc_physical_targets` lists them, plus `mode` (default `PN/IE`) and `expectedAddress`. TIA connects to the address the
project configures; `expectedAddress` must be one of those addresses, so a typo or a project for another machine stops
before anything goes online (`TargetMismatch`). PLCSIM interfaces are refused (`PhysicalRouteIsSimulator`): simulated
PLCs keep their own, ownership-checked tools.

## Authentication

- Logins and passwords are never answered (`PhysicalAuthenticationRequired`). A PLC with access protection, or a
  download that needs the configuration password, has to be handled in the TIA Portal.
- TLS: a certificate is trusted only when exactly one is offered and its SHA-256 equals `trustedCertificateSha256`.
  Otherwise the call fails with `PhysicalTlsUntrusted` and reports the offered public certificate under
  `onlineChallenges`; check it against the PLC out of band before passing its hash.

## Download

`plc_physical_download` needs `acknowledgeLiveTarget: true` and `expectedPlcName` equal to the selected PLC, and refuses
an unsaved project. TIA's questions are answered only from explicit acknowledgements
(`src/TiaMcp.Openness.V21/PhysicalDownloadRules.cs`):

| TIA asks | Answer |
| --- | --- |
| Consistent blocks, alarm text libraries | Consistent download |
| User management | Keep the online data |
| Reinitialize data blocks or keep actual values | Keep actual values, unless `acknowledgeReinitialize` and `acknowledgeStop` |
| Reinitialize data blocks (no alternative) | Only with `acknowledgeReinitialize` and `acknowledgeStop`; otherwise stop |
| Stop modules | Only with `acknowledgeStop`; otherwise stop |
| Start modules | Start with `startAfterDownload`, otherwise no action |
| Configuration password, anything else | Stop the download |

A failed download can leave the PLC with a partial program; the result says so. The operating state after a download
is not observed by this route.

## Not covered

Forcing, CPU mode control (RUN/STOP without a download), memory reset, logins and protected PLCs, gateways and routing
across subnets, and qualification on any physical PLC. Recording real-hardware evidence is open in the
[TODO](../TODO.md).
