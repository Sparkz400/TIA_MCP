# C04 retained runtime acquisition — 2026-10-20

Implemented an explicit local acquisition of an existing retained polling snapshot into an exact diagnostic experiment. The new `diagnostic_experiment_acquire_runtime` and `diagnostic_runtime_acquisition_export` tools preserve original source/instance identity, selected runtime mapping, exact plan/segment bytes, all committed rows, per-read time evidence, quality/errors/gaps and capture truncation. Sequential observations remain unresolved behavioral evidence; no online identity attestation or trace synthesis is accepted.

Production scope:

- `DiagnosticRuntimeAcquisitions` composes and revalidates the new acquisition and exact JSON/readable exports.
- `RuntimeCaptureAcquisitionSource` retains bounded original plan/segment bytes and complete decoded rows, pinning the selected prefix without consulting the current head.
- `RuntimeCaptureAnalysis.Analyze` delegates to a pure `Build` method and then publishes the identical existing report. Existing experiment revalidation and safe Markdown/style helpers are reused through internal visibility only.
- Root-owned Host facades reuse the already owned `RuntimeCaptureStore`; there is no second store lock or new network/device action.

Focused evidence is under `artifacts/c04-acquisition-wave3-20261020/`:

| Check | Receipt | Result |
|---|---|---|
| Managed diagnostics | `managed-04.log` | 43/43 groups; acquisition 85 checks, existing repeated 117, existing experiment 102 |
| Isolated Host build | `host-build-03.log` | 0 warnings, 0 errors |
| Actual Host acquisition MCP | `mcp-02/result.json` | 58 checks, two clean Host lifetimes, absent native workers |
| Independent request contracts | `python -m unittest discover -s scripts -p test_runtime_diagnostic_acquisition_contracts.py -v` | 2 tests |
| Real-browser report checks | `browser-02/result.json` | 29 checks; 1440/390 px; exact embedded JSON/GFM; no page errors, network requests or dialogs |
| Actual prior producers | `prior-01/result.json` | 66 checks; 4 runtime analyses/captures, 3 single/repeated reports; 387 original files unchanged |

The independent Python oracle decodes and hashes the original plan/segments, compares every raw row to the retained rows, and derives the synthetic expected sequential counts directly: 15 rows, 7 cycles, 1 matched/1 contradicted/4 unknown pattern cycles, 1 host error, 1 provider failure, 3 unsupported values, 2 skipped polls and 1 dropped result. Schema/original-chain mutation controls reject changed counts, rows, mappings, errors, gaps, original bytes and promoted claims independently of production rederivation.

Managed boundaries include an exact 3 MiB original chain accepted and 3 MiB+1 refused, more than 10,000 rows refused, global-quota refusal after a valid analysis child without a completed acquisition, invalid-request no-publication checks, unassociated-capture refusal, unchanged nonempty active prefixes after append/stop, restart, changed/missing original source and honestly rehashed derived tampering. The public MCP harness additionally replans the experiment to `Conv1` and verifies that acquisition of the `Conv2` capture refuses without publishing a child.

The browser screenshots were visually inspected for narrow-width wrapping and readable quality/uncertainty sections. The complete original JSON remains exact in HTML and GFM-rendered Markdown; hostile source text is displayed literally.

Compatibility uses actual `pw20w2s2/artifacts/all-01/runtime-session-mcp` and `diagnostic-repeat-mcp`, whose producer Core binary differs from the current binary. Old runtime analyses rederive identical IDs, new acquisitions retain those exact analysis IDs, and old single/repeated reports and combined JSON/Markdown/HTML export bytes remain unchanged. All original prior files are checked byte-for-byte afterward. Newly generated acquisitions/exports are clearly current artifacts and are not relabeled as prior evidence.

The initial focused run exposed an unclassified missing-chain-file exception; the new acquisition route now returns `RuntimeAcquisitionSourceUnavailable`. Its original failed log remains retained as `managed-01.log`; the existing 42 groups passed in that run. Later focused runs pass.

`summary.json` binds the focused receipts and binary hashes; `source-hashes.json` records the final lane source, contracts, harnesses, documentation and root-owned Host integration inputs. Managed validation uses isolated `build-04`; actual MCP/browser/prior validation uses isolated `build-03`. The production acquisition implementation is unchanged between these final focused checks.

This note records focused offline implementation evidence. Frozen aggregate/package and final source-binding acceptance are owned by the root coordinator. No Siemens/TIA/PLCSIM/device activity, scan-synchronized acquisition, online program/address equivalence, causal conclusion or successful-fix qualification is claimed.
