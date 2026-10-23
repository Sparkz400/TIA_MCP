# Package smoke catalog pagination repair — 16 October 2026

The native package smoke harness requested only 200 feature contracts and compared
that first page with the entire external catalog. With 248 current entries, this
failed before reaching any native checks, even when the Host and external contract
matched exactly. A synthetic client using the actual current catalog reproduces
the failure in `artifacts/prediction-fix-dev/package-before.log`.

`smoke_package` now uses the existing deployment catalog reader with explicit full
feature retention. Ordinary deployment reports keep their original compact form.
The shared reader checks page/total bounds, exact cursor progress, unique IDs and
stable catalog version/total. Package comparison checks every feature field, the
catalog version and tool bindings. The production catalog MCP verifier also calls
this same package comparison with deliberately absent native workers, so future
catalog growth is exercised by the portable runner.

Development evidence:

- `package-after.log`: both package regression groups pass. The actual 248-entry
  fixture reaches a deliberately failing fake native stage after complete metadata
  comparison. The second group rejects changed version/total/metadata, duplicate
  IDs, stalled paging, Boolean totals, target-probe claims, missing tools and an
  external version mismatch.
- `deployment-after.log`: all 17 existing deployment transport/report tests pass.
- `catalog-mcp.json`: actual Host returns 248 feature contracts and 155 tools;
  the package comparison passes without native worker availability.

These files are under `artifacts/prediction-fix-dev`. No full native release
bundle was built or smoke-tested. The fake native stage is an intentional stop,
not engineering or simulator evidence. Full-bundle installation, relocation,
native worker startup and rollback remain external gates. A portable-only bundle
still needs its own implementation and acceptance.

Final source/aggregate and Python-delta receipts are recorded in
[the combined repair verification](prediction-validation-20261016.md).
