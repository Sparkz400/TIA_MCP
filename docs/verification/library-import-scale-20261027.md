# Library import scale — 27 October 2026

This run qualifies a native 100-component planned import and pushes a 150 MB package of vendor-distribution size through intake, staging, preview and apply. Evidence: [`local.library_import_scale.20261027`](../../evidence/local.library_import_scale.20261027.json), produced by [`scripts/qualify_library_import_scale.py`](../../scripts/qualify_library_import_scale.py) at `316ef5b` on worktree Release builds of that commit. All 626 steps passed in 1 h 12 min on PCNico (TIA Portal V21, owned scratch projects only). These are observed timings on one host, not cross-machine guarantees.

**Real vendor bytes remain open.** The PAL/DMC ZIPs reviewed in [vendor intake](vendor-intake-review-20261025.json) are no longer on this PC, and nothing was downloaded. The package reproduces the byte volume and shape of DMC Open Library V17, the largest retained distribution (149,771,810 bytes, 29 entries). Its only valid native payload is the owned archive described below.

The 10,000-element traversal and checkpoint-cost part of the scale item is measured separately.

## Worker dependency

The first full run failed at change 124 of a single authoring session, while releasing component 62. TIA refused the change with `EngineeringOutOfMemoryException`: "The maximum number (500000) of instances for this TIA-Portal has been exceeded."

During that session:

- apply time grew from 2 s to 20 s;
- worker RSS grew from 61 MiB to 1.35 GiB;
- Portal RSS grew from 3.6 GiB to 9.3 GiB.

Openness proxies release their TIA instances only when finalized. The worker now collects them after every response (`65fd13b`, cherry-picked here as `8f913f6`). With that fix, every phase below runs in one Portal session.

`--recycle N` reopens the project in a fresh Portal every N components or changes. It stays available for older workers, and the smoke run exercised it. It was not used in the qualifying run.

## Source components

One owned scratch session authors 100 flat types, 50 SCL FBs and 50 PLC data types, each as two single changes: `library_create_type`, then `library_release_version` 1.0.0.

| 100 changes each | Prepare median / max | Apply first / median / max | Apply total | Checkpoint first → last |
| --- | ---: | ---: | ---: | ---: |
| `library_create_type` | 2.6 s / 4.9 s | 5.1 s / 7.7 s / 13.0 s | 742 s | 359 KB → 1.03 MB |
| `library_release_version` | 3.4 s / 6.6 s | 2.7 s / 8.0 s / 13.5 s | 788 s | 400 KB → 1.03 MB |

A single `library_publish` change then carries all 100 components:

- prepare 10.9 s, preview 490,179 bytes;
- apply 20.4 s, library checkpoint 147,478 bytes.

The resulting archive is 805,217 bytes. Peak RSS in this session was 133 MiB for the worker and 3.9 GiB for the Portal.

## Package intake

| Stage | Observed |
| --- | --- |
| Generated package | 149,702,909 bytes, 29 entries: the owned archive, a corrupt decoy `.zal21`, documentation, and a nested 27-entry toolbox ZIP with 25 random-byte executables (never executed) |
| Chunked upload | 2,285 × 64 KiB chunks in 42.1 s |
| Stage | 3.2 s; 155,736,855 expanded bytes, 57 files, 2 `globalLibraryArchive` candidates, 26 documents; no executable became a candidate |
| Input limit | a 1,082,356,395-byte package is refused at the 1 GiB `maxInputBytes` with `PackageLimitExceeded` in 1.4 s; the failed state is retained |
| Decoy archive | `library_open` refuses it with a structured `EngineeringError` before any project change |
| Real archive | opens in 2.1 s; complete metadata (202 items) is read in 4.6 s |

The quotas in force were the store defaults, which the run stayed below:

- 1 GiB input and 4 GiB expanded bytes;
- 20,000 entries and nesting depth 3;
- 64 KiB chunks and 200 items per page;
- 16 GiB stored and 128 packages;
- 600 s of work.

## Combined planned import

In a fresh owned scratch project, a 101-component selection is refused with `PackageLimitExceeded` ("Select at most 100 components in one change") before any write.

All 100 components are prepared as one `library_instantiate` change:

- prepare 8.5 s, preview 148,091 bytes;
- apply 22.5 s, checkpoint 359,341 bytes.

Afterwards the project shows:

- 100 `linkVerified` rows;
- exactly 50 blocks and 50 types added, with existing content preserved;
- 0 compile errors;
- the first and last version each used once.

Peak RSS was 4.0 GiB for the Portal and 92 MiB for the worker.

## Separate single-component imports

A third session applies the same 100 components as 100 separate `library_instantiate` changes:

- prepare median 3.7 s (max 6.1 s);
- apply median 8.0 s (max 36.8 s, last 9.9 s);
- apply total 829 s, about 37 times the combined apply;
- checkpoint growing from 359 KB to 1.05 MB;
- peak RSS 140 MiB for the worker and 4.0 GiB for the Portal.

`library_package_release` of the applied package is refused with `PackageInUse`, because the changes' recovery journals retain it.

PLC `library_integrate` (call, instance or signal plans) stays plan-only. Its combined apply is disabled and was not exercised.

## Scoped budgets

These are regression budgets for this host and profile (PCNico, TIA V21, owned scratch, 100 flat types), set at about 4× the observed values. They are not latency guarantees for other machines, projects or libraries with dependencies.

| Operation | Observed | Budget |
| --- | ---: | ---: |
| 100-component `library_instantiate` prepare / apply | 8.5 s / 22.5 s | 35 s / 90 s |
| Single-component change apply at ≤ 100 library types, median | ≤ 8.0 s | 30 s |
| Change checkpoint at ≤ 100 types | ≤ 1.05 MB | 5 MB |
| 150 MB package upload / stage | 42.1 s / 3.2 s | 170 s / 15 s |
| Worker / Portal RSS in one 200-change session | 133 MiB / 3.9 GiB | 512 MiB / 8 GiB |
| Changes in one Portal session | 200 | must not hit the 500,000-instance cap |

## Not covered

- real vendor bytes (see above);
- libraries with dependency closures, HMI components and master copies at this scale;
- PLC `library_integrate` combined apply;
- other hosts and TIA versions;
- the 10,000-element traversal.
