# FileStorage server terminal deadline — 24 October 2026

`Siemens.Automation.ObjectFrame.FileStorage.Server.exe` now has its own measured
terminal deadline in `qualify_force_capture.py`: 360 s. Every other owned process
keeps the caller's deadline (45 s in the native runners).

## Measurement

The retained headless runs on this PC each record when the terminal wait began
(`cleanup-live.json`) and when every original handle was terminal
(`process-exits.json`). In all 16 runs with a FileStorage server and no forced
cleanup, that interval was 300.0–301.7 s, which points to a fixed idle shutdown
of about 300 s. The deadline is that plus a 60 s margin. One visible-UI run
(`p24-ui2`, 647.6 s) is not a headless sample. The two forced runs
(`p24-startup`, `p24-startup02`) were excluded.

Each exit record now carries `terminalDeadlineSeconds` and
`observedExitSeconds`, and a deadline error names the late process.

## Qualification

`scripts/qualify_file_storage_exit.py` ran three times. Each run started one owned
headless Portal with a new scratch project in an owned process job, retained the
original handles and terminated nothing. The FileStorage server exited naturally
at 301.4, 301.5 and 301.4 s; every other process exited within 1.9 s. All three
runs passed with `normalLifecyclePassed: true` and no errors
(`evidence/local.lifecycle.file-storage.20261024.json`).

## Not covered

Requested native stop while work is running, project-lock release and a full
runtime batch. The deadline is a measurement on this PC; another machine or a
TIA update needs its own.
