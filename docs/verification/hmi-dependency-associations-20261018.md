# Explicit HMI component dependency associations

Status: implementation, focused checks and full frozen aggregate/package
validation pass. Git integration is recorded separately in the evidence directory. No Siemens SDK or native caller was built
or executed. The base is `c655fa9e4b1801cbf24fc61a4d9c154c5b77719a`.

The existing review/page tools now accept v6 dependency selections. Each selection
binds an original direct dependency index to an exact selected object in the same
before/after scope, using existing retained original FB export associations.
Unavailable observations, observed empty lists, contradictions, competing type
identities, repeated indices, self-references and explicit matching cycles remain
distinct. Missing exports do not become matches. All closure/native qualification
flags remain false. The [reference](../reference/hmi-component-review.md#explicit-dependency-associations-v6)
documents bounds, precedence and retained-record behavior.

Focused evidence uses the actual Core implementation, 11 retained install/update
packets and two clean actual Host lifetimes without a native worker:

- 35 evaluator checks cover exact observations, malformed/absent identities,
  mapping refusal, separate scopes, cycles, 20000 references and overflow.
- 26 retained-store checks cover original export retention, release/reopen,
  before/after scope isolation, changed files and rehashed forged reports.
- 864 MCP checks include strict schemas, paging/download, independent source and
  export comparison, six rejecting mutation controls, release and restart.
- All 19 original v1-v5 records pass 1132 compatibility checks, including refusal
  of the three new sections across two clean Host lifetimes.

Two failures were reproduced and preserved. `Guid.TryParseExact` accepted padded
saved GUID text; v6 now requires exactly 36 characters in both saved-association
stages. Historical v5 behavior/results remain unchanged. The expanded MCP harness
also retained completed transient uploads until it reached the production count
limit. It now releases the earlier batch after its reviews retain their originals;
the same expanded cases pass without raising limits or dropping tests.

An independent Python oracle reads original saved observations and original FB
exports, rebuilds dependency comparisons/coverage and uses transitive closure to
check cycles separately from the C# traversal. Forged agreement, dependency,
cycle, coverage, native identity and summary results each fail verification.
Synthetic saved observations do not establish native authenticity or behavior.

Evidence lives under `artifacts/hmi-dependency-associations-01/` in the primary
checkout. `guid-padding-control.log` and failed `mcp-03` evidence are retained;
`workflow-05`, `mcp-04` and `prior-01` contain the focused passing evidence.
Final validation is recorded below; detailed validation and Git receipts are
retained separately there.

Still open: full C01 consumer/content association, broader established-format
output expectations and authored generation; C04 executable provenance; D16
pressure/profile work; and the rest of the original 44-workstream plan. Genuine
missing formats, native SDK/callers, installation/regeneration, compile/preservation
and HMI runtime acceptance remain separate external gates.

## Final frozen verification

Two identical 2442-file source snapshots pass all 44 managed suites, 1520 Python
tests (one existing unavailable-evidence skip), 25 Explorer model/client tests and
all 38 package stages. Both freshly generated complete HMI fixtures pass 864 MCP
checks, including all 11 dependency packets, two clean Host lifetimes and the six
independent rejecting controls. Both runs pass 1132 compatibility checks over all
19 original v1-v5 records; their original stored files remain byte-identical.
The complete managed producer runs the 35 evaluator and 26 retained-store checks.

The package has 1759 files and manifest
`d2df6ba8b9a5f6a7f1e9923af1074841588bd9d6418367cc76f4a919c55a1ba5`. It includes the independent dependency oracle and passes relocated
MCP workflows. All five Explorer assets match the previously tested bundle; no
new browser run is claimed. Both runtime workflows independently verify seven
capture chains, three migrations and seven mutation controls.

All 112 historical evidence entries remain unchanged, including 106 unavailable
inputs. Original fixtures, historical schema definitions, qualification flags and
the original full-scope reconciliation are unchanged. Only final documentation
differs from the frozen snapshots. Source hashes, receipts and exact Git/CI status
are retained under `artifacts/hmi-dependency-associations-01/`. Hosted CI remains
subject to the account payment/spending restriction; local results do not establish
hosted execution, Siemens SDK compatibility or native qualification.
