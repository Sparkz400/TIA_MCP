# Diagnostic measurement quality and timing — 2026-10-20

The fourth C04 wave adds compatible v2 supplied measurement, comparison, single/repeated experiment and combined export paths through the existing diagnostic MCP tool names. It does not acquire native data or admit behavioral assertions. The [reference](../reference/diagnostic-measurement-quality.md) specifies the schema, exact-clock/epoch selection, quality rules, limits and version boundary.

## Focused evidence

All paths below are under `artifacts/c04-quality-wave4-20261020/`. The final focused Host is `build-09/bin/TiaMcp.Host/release/TiaMcp.Host.exe`. [Independent summary](../../artifacts/c04-quality-wave4-20261020/independent-summary.json) binds the receipts, source inventory and managed binaries.

| Validation | Result | Evidence |
| --- | --- | --- |
| Managed diagnostics | 44/44 groups; 83 new quality checks | [managed-08.log](../../artifacts/c04-quality-wave4-20261020/managed-08.log) |
| Host build | Exit 0, 0 warnings, 0 errors | [host-build-09.log](../../artifacts/c04-quality-wave4-20261020/host-build-09.log) |
| Independent contract boundaries | 4 unittest methods passed | [contracts.log](../../artifacts/c04-quality-wave4-20261020/contracts.log) |
| Actual Host MCP | 160 checks, 2 clean Host exits, no native worker starts | [mcp-02/result.json](../../artifacts/c04-quality-wave4-20261020/mcp-02/result.json) |
| Real Chromium HTML/GFM | 44 checks at 1440 and 390 pixels; zero page errors, HTTP requests or dialogs | [browser-04/result.json](../../artifacts/c04-quality-wave4-20261020/browser-04/result.json) |
| Actual wave3 package producers | 162 checks, 8 clean Host exits, all 298 files present at replay start unchanged | [prior-02/result.json](../../artifacts/c04-quality-wave4-20261020/prior-02/result.json) |
| Original prior baseline | Exact initial 289-file mapping unchanged; 9 additions distinguished | [prior-original-289-integrity.json](../../artifacts/c04-quality-wave4-20261020/prior-original-289-integrity.json) |

The actual prior roots are `artifacts/pw20w3s1/artifacts/package-01/diagnostic-mcp`, `diagnostic-experiment-mcp`, `diagnostic-repeat-mcp` and `runtime-diagnostic-acquisition-mcp`. These are retained outputs from the previous producer, whose Core hash differs from the current Core. The first replay preserved all 289 original files and added nine new exports. The final focused replay preserved those 298 files and added none. Original v1 analysis/comparison, single/repeated reviews, original exports and three original runtime acquisition records, including a prefix snapshot, rederive identically after Host restart. The independent acquisition oracle decodes original plan/segment bytes rather than trusting the derived rows.

## Independent vector and refusal coverage

The authored 12-group vector contains two source epochs and separately timed host readings. It includes one backward time, one duplicate time, an irregular gap, a missing reading key, a missing/null value, bad and uncertain statuses, a good value with an error, an unknown source timestamp, truncation and unknown dropped count.

The independently expected final-signal counts are seven usable values, five unknown values, three true/four false values, one reset, one duplicate time, one epoch change, one interval gap and exactly two eligible adjacent value changes. Its hypothesis has four matched and five unknown observation groups, always unresolved. The explicit first-epoch window has two matched and three unknown groups. Window exclusion counts separately retain absence and unknown time; complete-epoch assessment retains untimed observations. Explicit host/source selections and different per-pair clock mappings remain unverified.

The MCP oracle decodes and hashes every byte-exact original JSON input, compares it to normalized content, verifies exact original report bytes, independently checks the counts/times/statuses, and rejects deliberately changed original or derived evidence. It checks all three export formats for analysis, comparison, single and repeated reports. The browser independently compares every rendered individual status, value, error and timestamp against the decoded original JSON, verifies the complete embedded JSON and rendered Markdown fence, exercises keyboard table scrolling and bounds horizontal overflow. Desktop and mobile screenshots were visually inspected.

Managed and MCP controls refuse malformed/unknown/duplicate fields and domains, duplicate JSON names, wrong value types, invalid sequence order, unknown clocks/epochs, lossy time conversion, mixed v1/v2 composition, duplicate original runs and missing changed-instance mappings. Empty incomplete captures and absent/untimed readings remain representable. Caller-attested complete/atomic/program-matched quality still cannot produce a behavioral verdict. A missing expected interval disables adjacent-change inference without discarding valid individual value statistics.

After upload release and a fresh Host, analysis/comparison/single/repeated exports remain deterministic. Altered or missing owned original input and source review refuse. Rehashed normalized inputs, analyses, comparisons, single reviews and repeated counts refuse when they disagree with rederived original evidence. Invalid repeated requests publish no child artifacts. Explicit direct-replan instance mappings are retained through every pair and the combined export.

## Commands

```powershell
dotnet run --project tests/TiaMcp.Diagnostics.Tests/TiaMcp.Diagnostics.Tests.csproj -c Release --artifacts-path <isolated-build> -- <samples.json>
python -B -m unittest discover -s scripts -p test_diagnostic_quality_contracts.py -v
python -B scripts/qualify_diagnostic_quality_offline.py --host <Host.exe> --samples <samples.json> --output <fresh-output>
node scripts/qualify_diagnostic_quality_browser.mjs <MCP-output/result.json> <fresh-browser-output>
python -B scripts/qualify_diagnostic_quality_prior.py --host <Host.exe> --source D:/03_Entwicklung/TIA_MCP/artifacts/pw20w3s1/artifacts/package-01 --output <fresh-prior-output>
```

The actual-prior command intentionally uses retained external historical artifacts and is separate from self-contained package/aggregate runners. The final frozen aggregate and relocated package replay are coordinated by the root wave; focused evidence does not claim those future runs have completed.

## Retained failures and limits

The initial managed vector exposed an epoch counter increment at an absent timestamp. That implementation was corrected; both final managed and independent MCP vectors pass. `managed-02.log` preserves the initial failure.

The first browser run found page-level horizontal overflow from long heading hashes at 390 pixels. A v2-only heading wrap fixed it; the shared v1 style remains unchanged. `browser-01/result.json` retains the failure. The final browser harness also resets its keyboard-scroll probe before screenshots, so images show the initial table position.

No TIA, PLCSIM, physical CPU, native trace or external OPC UA endpoint was used. This evidence qualifies offline quality/timing handling and provenance preservation, not source authenticity, synchronized clocks, PLC event times, statistical independence, causal explanation or a successful bug fix.
