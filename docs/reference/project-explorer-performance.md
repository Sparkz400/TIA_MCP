# Portable Explorer performance acceptance

The repeatability gate measures the actual saved-file workflow at 50,000 nodes and
50,000 retained edges. It does not measure TIA capture, establish project-wide
relationship completeness, or qualify an actual desktop MCP Apps host.

## Acceptance and timing boundary

These criteria were fixed before implementation: ten fresh browser-process cold
imports and ten same-page warm imports, with **every** first viewport strictly
below the existing 1,000 ms target. No percentile replaces that requirement and
no miss is discarded. Every import also requires 1–69 rendered tree rows. The
independent tree, search, exact-reference, relationship and refusal checks must
pass without page errors or HTTP(S) requests.

Timing starts in a capture-phase listener for the real file-input change. It
includes file reading, JSON parsing, validation, indexing, UI construction and
two animation frames after `Explorer.load` resolves. Selecting the file and
starting the browser are outside the interval. Cold means a fresh Chromium
process/context/page; the operating system file cache is uncontrolled. Warm
means a second import in the same page, including all parsing and validation
again. The test does not relax or skip any production validation.

The former scale harness passed a large nested object through Playwright's
`evaluate` binding. Paired profiling found a much larger first-frame interval
on that path despite nearly equal validation/index time. Profiles include
automation reconstruction and garbage collection; their contribution to the
additional delay is an inference, not a precisely isolated attribution. It is
a different measurement from a user's local file import. Both old misses and
the paired diagnostic profiles remain valid retained observations; they are not
rewritten as successful imports. The standard browser scale checks now use the
same real file path as the repeatability gate. No production optimization was
needed to meet the measured portable-import target.

## Reproduction

Use a fresh evidence directory. The conditions argument is mandatory; record
whether competing builds, native workloads or other heavy work were paused.

```powershell
node ui/project-explorer/tests/browser-import-performance.mjs `
  --output artifacts/explorer-import-run `
  --conditions "Dedicated measurement window; no concurrent repository builds or native dispatch"
```

`--trials N` supports development runs, but fewer than ten pairs cannot pass
acceptance. `--viewer <absolute-reader.html>` exercises an existing portable
viewer, including a relocated package, instead of packing the current assets.
The report binds that actual HTML hash separately from the test/source hashes.
`PLAYWRIGHT_MODULE` and `EXPLORER_BROWSER` select installed test dependencies and
the browser executable when needed.

Each trial checks independently specified initial node IDs, all 50,000 retained
nodes/edges, the last object's exact search/reference, and the 1,000 expected
relationship IDs with unchanged source records, unresolved observations and
explicit edge truncation. Negative controls alter the observed graph, exercise
the exact timing threshold, and import malformed JSON, duplicate identities,
changed endpoints and a file above 32 MiB. Rejected files must preserve the
active model and selection. The output records every pair, source/fixture/viewer
hashes, machine/browser information, timestamps, conditions, page errors and
network requests. Fresh directories and exclusive final-result creation retain
earlier failures. A progress receipt is updated after each completed pair.

The ordinary browser suite remains the broader visual, interaction, export and
deterministic MCP-host regression. Its single scale samples are descriptive;
the separate repeatability command is the performance gate. Neither substitutes
for native collection or actual desktop-host acceptance.
