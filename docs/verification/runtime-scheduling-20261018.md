# Polling deadline and filesystem-denial checks — 18 October 2026

Base: `b897e1dce01606e2a910217a7d69733459903ebb`. Evidence root: `artifacts/runtime-scheduling-01`.

## Reproduced defect and correction

The poller previously relied only on cancellation callbacks for its duration limit. With the callback deliberately withheld and a selected-signal read advancing monotonic time by 1,500 ms, a one-second capture started four reads and ended after 6,000 ms with `sampleLimit`. The retained negative-control capture proves those four attempts; this was not inferred from a source search.

The poller now also checks monotonic elapsed time before each selected-signal read and at each cycle. The same test retains the first in-flight result at 1,500 ms, starts no further read and ends with `durationLimit`. In-flight I/O is cooperatively cancelled; this does not promise hard real-time interruption. Final gap accounting also records elapsed scheduled slots when cancellation or a late wake-up interrupts a wait. Gap counts stop before the requested duration boundary, and subsequent cycle numbers never reuse a slot already reported skipped.

A second negative test requests stop at 350 ms, then lets an already-started read complete at 850 ms. Gap accounting initially reported eight missed slots, extending beyond the requested stop. The stop callback now records monotonic cancellation time. The corrected report counts three missed slots while preserving the late read's actual 850 ms completion time; `stop-regression.json` verifies both retained results.

Clock injection uses the existing .NET `TimeProvider` APIs; production defaults remain `TimeProvider.System`. Original public constructors remain available. The existing first-future-slot helper now refuses an out-of-range previous cycle, preventing integer overflow on invalid input. There are no new dependencies, tools or wire-schema changes; older captures remain readable.

## Executed focused evidence

- `regression.json`: independently reads the retained failing and corrected captures, confirming four versus one read and the corresponding terminal reasons. The failing run contains the clock seam but lacks the monotonic guard; its production polling body is linked directly into the existing physical-runtime test project.
- `final-core-03.json.polling/polling-checks.json`: 40 deterministic checks cover fast and slow polls, exact slot selection, withheld deadline callbacks, delayed wake-up, cancellation while waiting/in-flight, late completion after explicit stop, UTC reversal/forward jumps, partial multi-signal capture, four-worker capacity, repeated intent, shutdown, timer disposal and sustained 64-signal timeout load through the byte limit. The load crosses multiple buffers, counts one rejected result, retains a normal final prefix and stays within the one-MiB budget. The authenticated-session boundary is a test double; polling/store code is the production source. The existing helper also has eight additional interval/hour/invalid-cycle checks.
- Seven Windows filesystem checks use an explicit create-file denial on one newly created test capture directory. An independent write probe receives `UnauthorizedAccessException`. Publication failure preserves the exact committed head/prefix, exposes unknown unpublished data, and permission restoration does not replay acquisition. Export remains local. The original DACL is restored in `finally` and compared afterward.
- Initial ACL run `after-04` failed in test cleanup because it attempted to restore an unchanged owner as well as the DACL. The isolated test directory's denial was removed and verified. Cleanup now restores only the changed DACL; `after-05` and `final-core` pass. No repository or system-wide permission was changed.
- Final focused physical-runtime suite: 261 checks; the same suite in the frozen aggregate passes 297 checks. These totals include existing concurrent status checks whose count can vary; the new 40 deterministic polling checks and seven Windows checks are fixed. Host Release build: zero warnings/errors. Actual Host/MCP with disposable TLS, current-user DPAPI and restart passed 644 checks in `mcp-01` before the final stop-window correction and 645 checks in final `mcp-02`.

## Aggregate and integration

The frozen package run exits zero and passes all 26 stages, including 642 runtime MCP checks with disposable TLS, DPAPI and restart after relocation. Its 1,488-file bundle has manifest SHA-256 `94f3d9747322897c7b4e90ddda2a313fc3c95ce0a8466ad1616c0d5ae0db80c1`. This establishes hash consistency, not a publisher signature.

Final aggregate and package source copies contain identical 2,113-file maps. Their manifest hashes are `d483726621c34720d8b158b2ec0f905bdf8b4335ae3de414e670fea6fa4722f6` and `6a31a806bdb8cf49c64f0b63d3f4d8b61932220d5b84a5f59042cf9d016f0249`, respectively (`aggregate-source-02-manifest.json` and `package-source-02-manifest.json`). Earlier source copies predate the stop-window correction and are not the final validation source. Only documentation changes are permitted after the final freeze.

The frozen aggregate exits zero and passes 43 managed suites, 1,464 Python tests (one expected skip), 24 Explorer unit tests and all configured MCP workflows, including 644 runtime MCP checks. The [validation receipt](../../artifacts/runtime-scheduling-01/verification.json) verifies identical frozen file maps, documentation-only changes afterward, both negative controls, and exact preservation of historical evidence inputs. The 106 unavailable historical inputs remain unqualified. `acl-cleanup.json` independently confirms that the earlier failed-cleanup directory has no explicit deny rule remaining.

The [integration receipt](../../artifacts/runtime-scheduling-01/integration.json) records the pushed commit, exact-commit hosted CI outcome and final branch/worktree state. Local test passes do not establish hosted CI success. No temporary Git branch or worktree was created for this increment. The next planned work is the X04 Add-In contract and then separate X05 Project Server and X09 Teamcenter contracts; the runtime gaps below remain open.

## Remaining boundaries

No Siemens installation, PLC or native runtime was used. These checks establish bounded host scheduling and one real OS access-denial case, not physical sampling accuracy, firmware behavior or hard real-time deadlines. A pending read can finish after the nominal deadline; its actual timing remains retained. UTC changes never drive the monotonic schedule.

RT-03 still needs a safely isolated actual disk-full experiment and broader sustained load/resource-pressure coverage. The capture's byte/store budgets and synthetic publication collisions are not substitutes for disk-full evidence. No suitable bounded disposable volume was available for this run, and the current Windows process is not elevated. Independent address/program association and qualified assertions remain RT-07 work. Other runtime packages and the full 44-workstream scope remain open.

API reference checked through Context7: [.NET TimeProvider overview](https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview).
