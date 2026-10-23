# Repeated diagnostic experiments — 20 October 2026

This C04 increment adds explicit repeated-run comparisons and combined JSON/Markdown/HTML export to the retained diagnostic experiment workflow. It uses supplied offline fixtures. No TIA, PLCSIM, simulator worker, or physical target was started.

## Implemented boundary

`diagnostic_experiment_compare_runs` accepts 2–8 explicitly named pairs with exact experiment/measurement IDs. Every pair rederives the original evidence before publication. It retains separate window observations and descriptive per-phase run counts. Poor quality, missing/unknown values, absent conditions, incomplete/empty windows, and absent predictions remain unresolved. Cohorts require the same exact before/after cases, relative windows, and compared mappings/units; anchors stay explicit. Distinct original hashes are required without claiming independent experiments.

`diagnostic_experiment_report_export` accepts both original single-pair v1 and new repeated reports. It reopens originals and rederives all retained children before publishing a bounded combined artifact. Raw original source IDs/bytes are preserved. A rehashed altered aggregate, window assessment, qualification flag, derived child, or normalized input does not establish valid evidence. Exact instance/replan mappings and the original Test Suite/alarm evidence remain visible.

The existing single-pair v1 producer remains byte-compatible; the internal pure builder permits complete validation before saving any child. The new contracts are additive. Storage/I/O failure can still leave valid content-addressed children without a complete report; invalid requests do not publish children.

## Final focused evidence

All paths below are relative to `artifacts/c04-repeat-wave2-20261020/`.

| Evidence | Result |
|---|---|
| `managed-final.log` | 42/42 offline diagnostic groups; 117 new repeated/export checks and 102 existing experiment checks |
| `mcp-03/result.json` | 78 actual MCP checks; two clean Host exits, no worker starts |
| `browser-04/result.json` | 26 Chromium checks, desktop 1440px and mobile 390px; zero page errors, remote requests, or dialogs |
| `prior-03/result.json` | 9 checks against the actual frozen previous producer; 46 preexisting files unchanged; two clean Host exits |
| `test_repeated_diagnostic_experiment_contracts.py` | 2/2 Python tests |
| Isolated final Host and diagnostics builds | 0 warnings, 0 errors |

The managed and public MCP tests use independently specified expected counts: one supported and one contradicted before run, two unresolved after runs, and exactly three retained matches in a poor-quality after run. They exercise seven further acquisition/window defects, a condition outside the requested window, empty windows, duplicate original wrappers, mismatched cohorts/windows/mappings, explicit second-instance replans, restart, honest rehash tampering, and missing/changed original inputs. Invalid repeated requests publish no child artifacts.

The browser consumes the actual MCP-exported artifacts rather than synthetic HTML. It checks exact combined JSON in HTML and GFM Markdown, literal hostile text, headings/evidence/lineage/quality sections, readable minimum column widths, no page-level horizontal overflow, and keyboard scrolling inside mobile table containers. Screenshots in `browser-04/` were visually inspected.

The prior producer is `artifacts/pw20src3/artifacts/all-01/diagnostic-experiment-mcp`, whose retained result identifies the earlier Host/Core binaries. The new harness requires a different Core hash, selects the recorded successful original v1 review ID, rederives that same ID, exports all three formats twice across actual Host restart, and verifies every preexisting byte. The first compatibility run preserved all 42 original prior files. Subsequent runs additionally preserved earlier exports, so the final manifest contains 46 files; those extra exports are not represented as historical producer evidence.

Final Host DLL SHA-256: `ca419054b594f6b5010cfd30d4d2ee1c3a8a8902b6139115540747e94f0942d6`.

Final Core DLL SHA-256: `4c8c6b78c6e84a5e582278ce72d9ef2793840f1e436ade01ec1a873ff24b4c32`.

Receipt hashes:

- Managed log: `08e7298c07e8a40d431e91759511ae6af6c1f18c05cc7aba40569ef4658d401c`
- MCP result: `1306de2f22eda5178780303bd4e69532c3d395a7872bda652b32c2f197fb0d2b`
- Browser result: `5e0798a35f2fc5f84b98f154505c1d03ab133e69ab368d64051ead04e30ec76c`
- Prior result: `f24521e266a1a7c24b74565c88651ddd2f48502d2f9184d5f6715a50b4a39eb0`

## Findings corrected during validation

`browser-01` exposed long hash headings overflowing the mobile viewport. `browser-02` confirmed the layout fix but exposed GFM autolinking escaped saved URLs. The renderer now wraps headings, uses bounded readable column widths, and encodes URL punctuation using the existing diagnostic report escaping convention. `browser-03` passed assertions but visual inspection caught a blank mobile top screenshot after the harness used the End key. The final harness tests horizontal ArrowRight scrolling and verifies scroll position before capture; `browser-04` passed with readable screenshots. These earlier attempts remain retained and are not counted as passing final evidence.

## Reproduction

```powershell
dotnet build tests/TiaMcp.Diagnostics.Tests/TiaMcp.Diagnostics.Tests.csproj --artifacts-path <isolated-build> -v quiet
& <isolated-build>/bin/TiaMcp.Diagnostics.Tests/debug/TiaMcp.Diagnostics.Tests.exe <samples.json>
dotnet build src/TiaMcp.Host/TiaMcp.Host.csproj --artifacts-path <isolated-host-build> -c Release -v quiet
python scripts/qualify_repeated_diagnostic_experiments_offline.py --host <Host.exe> --output <fresh-mcp-output> --samples <samples.json>
node scripts/qualify_repeated_diagnostic_experiment_browser.mjs <fresh-mcp-output>/result.json <fresh-browser-output>
python scripts/qualify_repeated_diagnostic_experiment_prior.py --host <Host.exe> --source <actual-prior-experiment-output> --output <fresh-prior-output>
python -m unittest discover -s scripts -p test_repeated_diagnostic_experiment_contracts.py -v
```

The root integration owns catalog/Host registrations and complete frozen aggregate/package runs. These focused results do not substitute for that combined validation.

## Remaining scope

No source authentication, physical sampling continuity, independent-run verification, clock synchronization, statistical inference, causal diagnosis, or successful fix is established. Quality is caller-attested and structurally checked. `completeRecordedSpan` only describes the span between the first and last recorded samples. Native acquisition, stimulation, and target-specific execution remain separate qualifications.
