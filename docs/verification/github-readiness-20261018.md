# GitHub dependency and repository setup — 18 October 2026

This increment changes repository configuration and documentation. It does not
upgrade dependencies or change application behavior. The full 44-workstream
offline goal remains active.

## Delivered and checked

- `.github/dependabot.yml` schedules weekly NuGet, npm, pip and GitHub Actions
  version updates. Minor/patch updates are grouped per ecosystem; major updates
  remain separate. Each ecosystem permits at most five open version-update PRs.
  There is no automatic merge configuration.
- NuGet directory patterns cover all 30 tracked active projects containing
  `PackageReference`, including native harnesses. Ten retained evidence projects
  containing package references are excluded. Both npm manifests, the Python
  test requirements and both workflows are covered. The dependency-free
  explorer manifest is included for future dependencies.
- The parsed YAML passes the downloaded SchemaStore Dependabot schema. The
  schema hash, exact manifest inventory and validation script are retained under
  `artifacts/github-readiness-01/`. This validates configuration locally; it does
  not prove GitHub has executed an update job or produced a usable PR.
- GitHub dependency vulnerability alerts changed from disabled to enabled;
  the independent readback returned HTTP 204.
- Six repository topics were applied and read back: `tia-portal`, `mcp`,
  `model-context-protocol`, `plcsim`, `dotnet`, `industrial-automation`.
- README badges link to the actual `main` workflows. Private repository access
  may be needed to view their status. Badges do not claim native qualification.
- Main protection was applied and read back. Normal PR merges require branches
  to be current, resolved conversations and these five checks, automatically
  bound by GitHub to its Actions app (15368): `.NET managed suites`,
  `OPC UA isolated parser and CLI`, `Portable package and relocation`,
  `Python contracts and harnesses`, and `explorer`. Force pushes and deletions
  are disabled. Stale reviews are dismissed. Required approval count is zero
  for the current solo workflow; independent approval must be configured when
  other contributors participate. Administrators retain bypass access, including
  the owner's already-authorized direct integration of verified increments.

## Hosted updater observation

After commit `dab0104`, GitHub started eight Dependabot runs. A retained snapshot
shows six successful runs, two still running and nine open dependency PRs
(npm, pip and Actions). This confirms actual PR creation; none has been reviewed
or merged by this increment. The exact run and PR identities are retained in
`artifacts/github-readiness-01/dependabot-execution.json`. Their branches remain
needed open work and must not be deleted as cleanup.

## Unresolved gates

Hosted checks on the preceding implementation commit were refused before any
steps because of account payment/spending limits. Normal PR merges requiring
those checks cannot pass until hosted execution is restored. This configuration
does not fix billing. The exact pushed commit's run results are retained in
`artifacts/github-readiness-01/integration.json` after integration.

The private vulnerability reporting endpoint returned 404. GitHub documents
that reporting feature for public repositories; `SECURITY.md` now makes the
fallback explicit. No dedicated private contact or end-to-end report submission
has been established. Secret scanning's endpoint explicitly reports disabled;
the account plan was not returned. No paid feature, plan or repository
visibility was changed. Dependency alerts do not establish secret scanning.

The subsequent [repository/history audit](repository-history-audit-20261018.md)
records technical inspection and remaining publication-review gates. No historical evidence, immutable
scope record, dependency lockfile, source binding or native qualification was
changed. Runtime tests were not repeated for these configuration/documentation
changes; the preceding [receiver verification](addin-receiver-20261018.md)
remains separate evidence for that implementation.

Native dependency update PRs need affected Siemens builds and qualification.
The PR template now calls this out. Offline CI alone cannot approve them.

## Evidence and references

- Local configuration check: `artifacts/github-readiness-01/validation.json`,
  `validate.py`, and `dependabot.schema.json`.
- Actual settings responses: `settings.json`, `main-protection.json`, and
  `main-protection-request.json` in that same artifact directory.
- [Dependabot configuration reference](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference).
- [Branch protection API](https://docs.github.com/en/rest/branches/branch-protection#update-branch-protection).
- [Private reporting availability](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository).
- [Secret scanning availability](https://docs.github.com/en/code-security/concepts/secret-security/secret-scanning).

Next: review dependency PRs and continue the C02/C03 composite workflow
contracts and the remaining operation/profile work. Billing, native
installation and genuine fixtures remain separate requirements.
