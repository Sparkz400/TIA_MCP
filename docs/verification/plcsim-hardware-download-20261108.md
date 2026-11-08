# PLCSIM hardware download and its configuration event — 8 November 2026

| Evidence | Runner | Result |
| --- | --- | --- |
| [`local.plcsim.hardware_download_watch.20261108`](../../evidence/local.plcsim.hardware_download_watch.20261108.json) | `qualify_plcsim_gaps.py --hardware-download` | passed |

X13 row 5 had native evidence for the software-configuration event only: `simulation_download_online` never downloaded
hardware, so nothing could raise a hardware-configuration change. The download now takes an optional `hardware: true`,
which adds `DownloadOptions.Hardware` to a complete software download. It needs `softwareScope: "all"` and
`allowStop: true`; otherwise prepare refuses with `InvalidRequest`. The download-configuration table is unchanged.

On an owned Softbus CPU 1511 instance, after `hardware_cpu_memory_bytes` enabled the system and clock memory bytes:

| Step | Result |
| --- | --- |
| Download configurations | `StopModules` StopAll, `ConsistentBlocksDownload`, `UserManagementDownload` kept, `AlarmTextLibrariesDownload`, `StartModules` StartModule |
| Download | Success, 0 errors; "Hardware configuration was loaded"; RUN before and after |
| `sim_observe` watch across the download | one `hardwareConfiguration` event (`OK`), with an `ipAddress`, two `softwareConfiguration` and operating-state/LED events |
| Values | `ProgramVersion` 1; the non-retained `Ob82` restarted from 0 (was 7) |

Not covered: module changes, other CPUs, TCP/IP instances; the rest of the PLCSIM gaps corpus did not rerun on this build.
