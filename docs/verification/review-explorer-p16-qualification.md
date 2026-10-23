# Review and explorer acceptance — 12 October 2026

The native dependency-impact fixture passed on frozen Release build
`fa0067bcbd32499485b72d8ea1cef38a`. This record does not claim desktop rendering,
PLC execution, complete project dependency coverage, or qualification of later
binaries. Original failed attempts remain unchanged.

After a separate HMI audit found contradictory native compile summaries, a
supplemental diagnostic audit (`artifacts/p16r/compile-diagnostics-audit-01.json`)
checked all fifteen retained review/scale compile objects and 1,061 messages.
All have zero-error summaries, successful target states, complete nontruncated
messages and no Error-state diagnostics. Original report hashes remain unchanged.

## Native review fixture

[`qualify_review_impacts.py`](../../scripts/qualify_review_impacts.py) creates four
SCL functions in a new owned offline project: `ReviewRoot` calls `ReviewLeft` and
`ReviewRight`, and both call `ReviewLeaf`. The leaf changes from `Int`/literal 1 to
`DInt`/literal 2, with a corresponding source marker. Both programs compile.

The successful report (`artifacts/p16r/rv02/result.json`) verifies:

- Twelve exact dependency paths to the leaf in each saved snapshot. The native
  provider exposes both `calls` and `dependsOn`, retaining the distinct paths
  through both branches and the unmodified evidence for every edge.
- Separate before/after review identities and exact native endpoint IDs, source
  locations, origins, and content-coverage limitations.
- Explicit truncation for depth, path-count, and edge-visit bounds.
- Unchanged complete peer XML, checkpoint recovery, and fresh reopen of both
  the changed and recovered projects.
- Identical persisted comparison, searchable source and health reporting after
  restarting the host without launching a native worker.
- Exact exits for the three owned Portal processes (46660, 63900, 21660), their
  workers and hosts.

The separate strict audit (`artifacts/p16r/rv02-strict-audit.json`) re-reads
45 retained exports and performs 36 complete XML comparisons, including an exact
leaf-change oracle. Only its return datatype, literal and marker differ. IDs,
comments, processing instructions, significant whitespace, namespace bindings
used by qualified values, and opaque payload are preserved. The comparator
excludes native top-level export provenance and serializer indentation only.
The saved review tool itself retains its separately documented comparison policy.
The leaf oracle also derives its expected edit through a preserving DOM, so a
namespace used only in an opaque QName value cannot disappear during the
comparison. Its supplemental audit (`artifacts/p16r/rv02-dom-leaf-audit.json`)
passes against the original retained exports and pins the unchanged report hash.

The first attempt (`artifacts/p16r/rv01/result.json`) remains failed: its
initial oracle incorrectly required only four call paths per side and omitted
the additional dependency-edge combinations. Exact owned cleanup passed. The
corrected fresh attempt and supplemental audits do not rewrite that result.

## Preservation helper correction

An added negative test proved that the previous shared qualification hash used
ElementTree's default parser, losing XML comments and processing instructions.
`qualification_owned_scratch.native_hash` now uses the existing strict native
comparator. New snapshots identify `native-payload.v2-comments-pi-qnames`.
Historical snapshot hashes are not rewritten or interpreted as the new policy;
supplemental audits recalculate from the retained raw exports.

## Function-heavy workload runner

[`qualify_project_explorer_functions.py`](../../scripts/qualify_project_explorer_functions.py)
authors exactly 1,000 distinct SCL functions. Each bounded batch runs in a fresh
owned TIA lifetime, preserving the existing 100-declaration limit and recording
compile/save and exact process cleanup before admitting the next batch. It never
replays a failed or uncertain batch. An optional stop file stops at a clean
boundary and records the remaining names.

The independent acceptance oracle requires complete native inventories, exact
function names/types/scopes, all explorer search and child pages, populated
authored relationships in both directions, independently checked source samples,
original peer XML, a final fresh reopen, exact last-function details, legacy index
compatibility, and hash-verified JSON/HTML exports. Source sampling is explicitly
separate from complete metadata coverage. Native capture and cached query timings
are measured separately.

Native execution status is taken from the resulting report, not runner presence.
The historical 500,000-Openness-instance failure and its recovery remain retained.

The completed native run (`artifacts/p16r/sc01/result.json`) passed all ten
batches and the final fresh reopen on frozen build `fa0067bcbd32499485b72d8ea1cef38a`.
Its independent final audit (`artifacts/p16r/sc01-audit-final-01.json`)
verifies all 1,000 function identities, 30 source samples, unchanged baseline
payloads, authored relationship evidence, exports, and twelve exact owned TIA
lifetimes with worker/host cleanup. The final metadata capture contains 1,070
nodes, with no metadata truncation. Native capture took about 4.17 seconds;
25 cached queries had a 1.39 ms median and 3.46 ms p95 including MCP transport,
excluding report persistence. These are local measurements.

Complete global relationship coverage remains open: the relationship provider
reaches its existing traversal bound. The audit records this separately from
the passed authored-edge and complete metadata/lifecycle gates. The run does
not prove every generated function's source payload or runtime behaviour.

The native-workload browser run (`artifacts/p16r/scale-browser-01/result.json`)
passes six checks using the installed Chromium: all 1,000 names across search
pages, bounded rendered rows, exact final-function selection/details, authored
call/instance/read/write edges and evidence, directional filters, SVG export,
and light/dark layouts. It records no browser errors or network requests and
pins the source snapshot, current UI, harness, package and screenshots. The
1440-pixel last-function and 1024-pixel dark relationship screenshots were
visually inspected. This portable viewer evidence does not qualify the actual
desktop MCP Apps host.

A second native-workload browser run (`artifacts/p16r/scale-browser-02/result.json`)
also passes all six checks after strengthening the oracle to compare the entire
SVG evidence/coverage manifest and assert page width at both viewport sizes.
An independent agent verified the first run's actual files already retained all
15 native edges and partial coverage; the added assertions make these explicit.

## Desktop and regression boundaries

The enabled Computer Use inventory returned no apps or browser surfaces in this
session. No desktop MCP Apps rendering pass is claimed. The 21 existing detached
explorer client/model tests pass; they do not substitute for desktop rendering.
An additional Playwright CLI attempt created a hash-manifested synthetic viewer
at `output/playwright/p16-explorer/fixture.html`, but the browser rejected `file:`
navigation. Automatic approval review rejected starting its loopback-only HTTP
server with `blocked by policy` and no detailed reason. The server was not
started and the owned browser session was closed. This attempt supplies no
rendering acceptance and does not replace the missing desktop host.

The user then installed project-local `@playwright/test` 1.64.0 and explicitly
requested its use. Node resolves the package from this repository; Chromium,
Firefox and WebKit all launched successfully. The existing explorer browser
runner used the installed Chromium directly, without a test HTTP server, and
passed 24 checks (`artifacts/p16r/browser01/browser-results.json`) with no
page errors or network requests. The
installation receipt (`artifacts/p16r/browser01/installation-receipt.json`)
pins the package/lockfile, browser versions, harness and report hashes.

The browser run covers native-reference identity in synthetic fixtures,
relationships, unresolved evidence, structured previews, keyboard navigation,
responsive light/dark layouts, exports, stale/disconnected states, the MCP App
parent-protocol fixture and tampered artifact rejection. Synthetic 1k/10k/50k
trees retained fewer than 70 rendered rows, with viewport times of approximately
35/124/421 ms. The 500-node graphs rendered in approximately 150–182 ms. These
are local measurements while native qualifiers were running, not guarantees for
other hardware or proof of a 1,000-function native workload.

The relationship and 1024-pixel dark screenshots were visually inspected. The
browser harness now defaults to new run directories, rejects existing evidence
directories, and prefers the locally installed Chromium. Root npm scripts
`test:explorer` and `test:explorer:browser` run the actual repository checks.
The generated `e2e/example.spec.js` remains a separate installation example.
Desktop MCP Apps product rendering is still a distinct outstanding gate.

A second current-UI run (`artifacts/p16r/browser02/browser-results.json`)
also passed all 24 checks using automatic selection of the project-installed
Chromium. Its source receipt (`artifacts/p16r/browser02/source-receipt.json`)
binds the updated runner and HMI binding filter correction. The HMI-specific
native binding acceptance is recorded separately by that workstream.

The HMI workstream also passed six browser checks on actual captured native
data (`artifacts/p16h/browser-current-01/browser-results.json`), including
populated binding/alarm relationships and light/dark layouts. Its corrected
current renderer is bound separately from the frozen HTML export, whose
original HMI filter failure remains retained. See [HMI evidence](hmi-qualification.md).

The first combined frozen build passed with zero warnings/errors. Its
full regression (`artifacts/p16r/full-regression-01.log`) passed 518 Python
tests across 37 suites (one existing skip) and all C# suites, including 144 core,
22 hardware-preservation, and 27 simulator self-checks. The simulator API was not
initialized. Later harness/preservation refinements require their own recorded
regression and do not change this source/build identity.
