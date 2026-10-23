# Portable package verification — 16 October 2026

The `-Offline` build/package path compiles the Host and portable libraries without
Siemens SDKs. It reuses isolated source/output manifests, strict bundle validation,
the external configuration generator and launcher. Full engineering packaging
remains the default. A build-time Host constant disables native bridges even when
the portable executable is invoked directly with existing worker paths.

The bridge gate runs before starting transport and does not fault the bridge;
repeated calls return `NativeWorkersDisabled`. Native qualification/diagnosis
flags are refused before Host startup. Regular builds keep their existing default
and can opt into the same behavior with `--no-native-workers`.

Portable bundles include the full metadata catalog, document resources, NuGet
license/notice inventory, immutable file hashes, ZIP and external ZIP SHA-256.
They require the appropriate .NET 10 ASP.NET Core shared runtime. Local VCI
requires a separately installed, explicitly selected Git executable; no Git or
Siemens binary is redistributed. Network inventory remains a separate explicit
endpoint capability, so native disabling is not a network isolation claim.

## Development checks

`artifacts/offline-package-dev` retains the first Host build, 151 passing core
checks and deployment test progression from 102 to 130 checks. The first package
has manifest SHA-256
`baad4caab24749171d4fd14e969876321e2b69ddc58046ff84f0f23c60d06ee4`
and 1,289 files. `check01` passes actual MCP via external configuration and direct
Host, local Git upload/commit/restart, exact ZIP/file/license comparison, repeated
native refusals, Explorer resources and relocation into a Unicode path. Reused
GRAPH (43), OPC UA (48), VCI (72) and translation workflows pass on the relocated
Host. This was before the final checksum, diagnostic runner and CI additions.

## Final validation

Both final runs use Git tree `89336d6197a27ec4171104f91331aad9ae2f76d6`,
archived under `artifacts/offline-package-01/source` (1,786 files). The adjacent
source manifest has SHA-256
`d3ececf9fca6769525615e91cd5960938b3c40fb8bfad68bfcc9b5721a4f40b4`.

`Test-OfflinePackage.ps1` passes all eight stages. Its receipt is
`artifacts/offline-package-01/package-checks/result.json`, SHA-256
`2200622e572559f07a3fc7409653431495de50ae104d92a47fb1b3c833939c3a`.
The exact bundle is
`source/artifacts/releases/tia-mcp-offline_20261016_151005_2401b210` beneath that
evidence directory. It contains 1,292 manifest-listed files and three Host NuGet
package entries. Manifest SHA-256:
`bb7de602c1a671d1699b25ee3300ddf87dfbc75565d026f4e6258a0608f8132c`.
ZIP SHA-256:
`4c8d843b315d235f6728c38eba42cd3a9693cafded813b8a590fcbab14d456e9`.

The package run includes:

- 133 deployment checks: legacy/default engineering behavior, local and encoded
  SSH argument boundaries, Git forwarding, path/tamper/unlisted/native-worker
  rejection, required notices, mode restrictions and actual junction refusal.
- Original and relocated package MCP checks: full 248-feature catalog comparison,
  Explorer HTML resource delivery, repeated native engineering/simulator refusal,
  subsequent portable calls, uploaded raw bytes committed through configured Git
  and recovered after restart. Each location has three clean Host lifetimes;
  no native worker exit receipts appear. Direct launch omits the disabling flag
  and supplies existing executable worker paths, proving the package default.
- The existing GRAPH (43 checks), OPC UA (48), VCI (72) and translation workflows
  against the relocated Host. Diagnostics pass 25 managed fixture groups and
  24 MCP steps across three Host lifetimes, including saved reviews, watch planning,
  imported measurements, downloads and restart. The diagnostic receipt SHA-256 is
  `105a6aa6152db66273cc69ca8355287115bd516557e2724349923d6c58b4f4ba`.
- Strict re-verification after artifact writes; the bundle remains unchanged.

The full aggregate on the same frozen tree also passes: all 29 managed suites,
1,266 Python tests (one unavailable-native-evidence skip), 24 Explorer model/client
checks and every actual MCP workflow. Core passes 151 checks, including disabled
bridge/disposal behavior. CLI tests preserve regular-build defaults and reject
native qualification modes when disabled. The aggregate receipt is
`source/artifacts/checks/result.json`, SHA-256
`8b6af39704c0ed67268963764f007cab39d0c09f93c412bfcbeacffedee64b07`.
Source bindings remain 175 declared tools, 55 mutation kinds and 248 features;
retained evidence stays six matching files and 106 unavailable files.

Only documentation was changed after the frozen validation tree. No production
code, scripts, project files, workflow configuration, contracts or tests changed.
Commit `2f5f65c` now passes all hosted jobs: Python, managed, portable package
and OPC UA in `38922940258`, and Explorer in `38922940170`. No extra branch or worktree was created; evidence snapshots and
the validated ZIP remain under `artifacts/offline-package-01`.

## Limits

No Siemens SDK/full native bundle build, native worker qualification, PLC or
simulator runtime, full installer/rollback, remote SSH execution or real desktop
MCP client acceptance ran here. MCP resource delivery is not visual acceptance.
The package manifest is a consistency check, not a publisher signature. ZIP
timestamps and unique build paths mean bit-for-bit reproducibility is not claimed.
The remaining L06 obligations and other workstreams stay open in the
[offline execution queue](../plans/offline-execution-20261016.md).
