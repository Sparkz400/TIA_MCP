# Supplied safety evidence workflow — 17 October 2026

This increment delivers the portable SA-03 portion of D14 through two explicitly
registered Host tools, a strict parser/comparison, immutable retained workflow,
versioned schemas, managed fixtures, actual MCP tests and package registration.
It builds on compiler-scope commit `43ed561` and preserves all 106 unavailable
historical evidence bindings. No native qualification is promoted.

## Scope and focused evidence

The [workflow](../reference/safety-evidence-review.md) retains exact original
JSON, UInt64 signatures and opaque supporting files. Saved project/PLC/block
association, duplicate/malformed input, source bounds, tamper, forged records,
partial warnings, unknown fields and byte-bounded paging are covered. Reopening
rebuilds the report from the saved selection and original inputs. Supporting
sources are revalidated after upload release and process restart.

The initial focused suite passed 93 managed checks before the final per-source
and total-source byte-bound additions. The actual Host build had zero warnings
or errors; `artifacts/safety-evidence-01/mcp/verification.json` passed 84 checks
across two clean Host lifetimes with absent native workers. These are preliminary
checks. An additional actual MCP probe against frozen tree `27ffe0b` found
explicit JSON null was rejected for optional supporting sources. The failed
probe is retained at `artifacts/safety-evidence-02/optional-sources/verification.json`.
The Host boundary now accepts omitted/null/empty forms equivalently; the permanent
MCP harness covers all three and passes 87 checks under -OO in
`artifacts/safety-evidence-01/mcp-optional-fixed`.

The earlier `safety-evidence-02` package passed all 23 stages, but its aggregate
stopped at 207/208 general checks: `PackageStorage.Save` received Windows access
denied while replacing an upload manifest. The exact same binary subsequently
passed 208/208 in `artifacts/safety-evidence-02/general-recheck.log`. An actual
file-lock probe reproduced the overwrite error, and also showed the existing
replacement helper does not retry that sharing code. The cause of the original
lock is unconfirmed. Original failure evidence remains retained; shared manifest
publication reliability is a separate open repair, with investigation at
`artifacts/package-save-investigation/`. Neither recheck nor later success erases
that failed aggregate.

Final frozen Git tree: `298c5957cd6b4792125ee9d344ef548501b38c6e` (2018 files).
[Source manifest](../../artifacts/safety-evidence-03/source-manifest.json) SHA-256:
`d495757989d83238c8295fb375195cd9bc4e62642c70b22ce1176ae3bec01301`.
Source ZIP SHA-256: `94ae8a22def297a578c4a518c2286aa30386b06d98ac9c71654cb81ff6049d21`.

The [aggregate](../../artifacts/safety-evidence-03/source/artifacts/checks/result.json)
passes 41 managed suites, 1,400 Python tests (one unavailable-evidence skip),
24 Explorer model/client checks, contracts/source bindings and all offline MCP
workflows. Safety evidence passes 96 managed checks and 87 actual MCP checks;
the existing general executable passes 208 checks. The focused MCP harness also
passes 87 checks under -OO in `artifacts/safety-evidence-01/mcp-optional-fixed`.
Run: 2026-10-17T11:53:27.8571289Z to 2026-10-17T12:08:24.9294632Z.
Aggregate receipt SHA-256: `226e66d6835a2c5aec8342ca05ffdb8ab3a91852a7d00ae6da3f24f1622961ae`.
[Console evidence](../../artifacts/safety-evidence-03/aggregate-console.log).

All 23 [package stages](../../artifacts/safety-evidence-03/source/artifacts/package-checks/result.json)
pass, including the same 87-check safety workflow through the relocated portable
Host. Bundle `tia-mcp-offline_20261017_115306_eb1c2003` contains 1427 manifest files.
Package receipt SHA-256: `2b04310fee07698bd91c0e7821c21a7bdd6570e978c34471e30ba5bf84720072`.
Manifest SHA-256: `6bb12f157d75abdcd9f8370f2b7330a43fff99c52578e87ee5b900671f382abe`.
ZIP SHA-256: `42ad1170e684e724fc6f0e7efd8a978e57cdfcc3db0a69aa6860d70527affd3e`.
The portable package disables native workers; it is not an SDK build or native
safety qualification. Browser tests were not repeated for this Host-only increment.

The [source comparison](../../artifacts/safety-evidence-03/final-source-comparison.json)
verifies the frozen source remained unchanged and permits only completion docs
to differ in the final commit. All 106 unavailable historical evidence binding
rows remain unchanged. The [integration receipt](../../artifacts/safety-evidence-03/integration.json)
records the pushed commit, exact hosted CI outcome and cleanup.

The full [D14 plan](../plans/expansion-plans/safety-validation-and-fio.md) retains
all twelve work packages. V21 SVA Openness excludes test execution; this workflow
does not invent that API. Current inputs are synthetic, not genuine vendor
printout/XLSX qualification. SDK build, native report capture, native identity,
required product signature profiles, F-I/O mutations, test management and
functional-safety acceptance remain open. Browser and physical equipment tests
are outside this increment.
