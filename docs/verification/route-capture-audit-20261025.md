# Independent audit of the typed route capture — 25 October 2026

Audited evidence: `evidence/local.typed.route-capture.20261025.json` (lane A, PR #60).
The audit was read-only and independent: a separate agent that had not seen lane A's
conclusions checked the retained raw files on PCNico (`D:\ta\w\artifacts\cap3`). It ran no TIA,
PLCSIM, Host or worker.

**Verdict: pass, with the gaps below.** All seven points passed. Nothing in the raw files
contradicts the curated summary.

| Point | Result | Basis |
|---|---|---|
| Report hash, commit, binaries | pass, gaps 1–3 | `curation-capture.json` matches the summary (sha256 `895318ed…`, 458,543 B). `typed_resolution_curation.py --capture` regenerates it byte for byte from `cap3/capture.json`. The five harness hashes match `f267de5`, and the binary tree hashes match a recomputation. |
| All 41 inputs used, in order, hash-matched | pass, gaps 4–6 | 3 prerequisites, then 38 packet inputs, in manifest order. One prepare and one apply each. Each file matches its manifest hash. The 10 XML inputs were imported with only the culture retargeted (de-DE → en-US). |
| Clean compile, save, exactly the declared blocks added | pass, gap 7 | Per-step before/after snapshots with matching manifests show exactly the declared blocks added (48 in total) and nothing removed. The Host reports compile 0/0, `applied` and saved for every step. |
| Original objects unchanged | pass, gap 8 | Every pre-existing object's XML is byte-identical after each step, apart from the export `<Created>` timestamp. after(N) = before(N+1) for all 40 transitions. |
| Saved equals fresh-process reopen | pass | The last step's after snapshot, the saved snapshot and the reopened snapshot are equal (58 objects). The reopen used new Host, worker and TIA processes, after the first TIA's exit was verified. |
| Exact cleanup | pass, gap 9 | Both lifetimes cleaned up with no errors. The Host and the worker each exited 0, neither forced. Each TIA exit was verified through a handle captured at start. The harness has no path that kills a Siemens process. |

## Gaps

1. **Raw evidence isn't bound.** Nothing committed hashes the raw report (`cap3/capture.json`, sha256 `4a85301b…`, 209,520,143 B), the snapshots or the exports. Only the reduced curated report is hashed.
2. **Commit and build are inferred.** The raw report records no commit; `f267de5`/`dirty:false` come from the curator's git call afterwards. With no build receipt, tying the binaries to that commit rests on file times. The tree hash also covers 8 `.pdb` files the run didn't hash.
3. **Helper modules aren't hashed.** Only five harness modules are hashed; the helpers they import were not hashed at run time.
4. **The packet manifest wasn't followed as written.** It still declares archive `82cdd36a…` (tgr04) and source packet `tgr06.inputs`, neither of which was used or retained. The run used the rebuilt baseline `39cea56a…`. Both manifests still say `authored-not-executed`.
5. **The manifest link comes from this audit.** The script doesn't read the manifests, and its command line isn't recorded.
6. **XML inputs were checked by line diff.** They were imported in a culture-retargeted form, which this audit compared only by line diff.
7. **Per-step compile and save are reported by the Host.** Only the final persisted state is independently confirmed, by the reopen.
8. **Preservation covers blocks, PLC types and tag tables.** Hardware, PLC and unit properties, system constants, watch/force tables and text lists are not compared.
9. **Cleanup has gaps:**
   - no TIA exit code is recorded;
   - only the main Portal process is tracked, not its helpers;
   - `projectLockReleaseVerified` is false in every receipt;
   - the worker exit receipt is reported by the Host itself;
   - the detach receipts say `ownsPortalProcess:false`.
10. **Earlier attempts aren't in the summary.** This was the third attempt:
    - cap1 was blocked at an apply and recovered.
    - cap2 captured all 41 inputs but failed cleanup: Host exit 1, worker not verified within the old 30 s budget.
    - cap3 ran with the 120 s budget (#55); its worker needed 52 s.
11. **The "19 inputs" aren't pinned anywhere.** The checklist's 19 aren't identified, but all 38 packet inputs were captured, so any subset is covered.

The checks are rerunnable. The audit's scripts and per-point results are kept with the integrator's
session notes, not in this repository.
