# TODO integration and portable package — 23 October 2026

This increment completes library export/manifest contracts, expands inspection
coverage, repairs exact decimal preview comparison and refreshes the API source
inventory. The library checklist now records the already-qualified configured
Unified fixture prerequisite separately from production/native acceptance.

## Completed checks

- `artifacts/todo-20261023/offline-02/result.json`: the complete offline aggregate
  passes 1,712 Python tests, 45 managed suites, 25 Explorer model/client checks
  and every configured Host workflow. Both Python optimization modes pass under
  their existing deadlines. The final UA diagnostic binding workflow passes
  928 checks. Transcript: `artifacts/todo-offline-02-20261023.log`.
- `artifacts/todo-20261023/package-01/result.json`: all 53 package stages pass,
  including strict ZIP/manifest verification, original and relocated startup,
  exact Git operations, retained-producer compatibility, credentials, encrypted
  loopback sessions, polling/subscriptions and diagnostic bindings. Transcript:
  `artifacts/todo-package-20261023.log`.
- The [library-specific verification](library-export-observations-20261023.md)
  records all 50 focused tests, original native observation compatibility and
  actual relocated-package validator execution.

## Exact package and implementation

Source snapshot: `artifacts/deployment-build/8f6e8c6b97874f0c971d48cf8638f6e0`,
containing 2,837 captured input files. Package:
`artifacts/releases/tia-mcp-offline_20261023_051612_6e6a9a84`, with its adjacent
ZIP and checksum. The strict package manifest SHA-256 is
`e28ea8a37ade46250aadc0d32d2aa63919d8f668af81ea33905a0d73474af18e`.
Its 2,093 packaged files remain immutable; no installed MCP replacement occurred.

`artifacts/todo-20261023/snapshot-comparison.json` compares the working source
with the frozen package inputs. Implementation, tests, schemas and fixtures
match; only documentation records changed after the snapshot. The aggregate
uses a separate build of that implementation. Exact focused evidence, source
and final result hashes are retained in
`artifacts/todo-20261023/verification.json`.

## Retained failures and remaining work

The first aggregate stopped because seven observation schemas were absent from
the anchored API inventory. Its failed result remains at
`artifacts/todo-20261023/offline-01/result.json`. Refreshing the inventory changed
only `sourceInputs`; feature counts and qualification claims are unchanged.
Four reproduced decimal-rounding/underflow failures are preserved in
`artifacts/todo-decimal-baseline-20261023.log`. The earlier Git path aggregate
also remains failed under its original source identity.

Read-only GitHub checks found that the latest recorded Python/.NET and Explorer
runs were refused before execution for account payment/spending-limit reasons.
Those runs reference `85bfd65e01422680a8388d0ca70ad4b5c6a19152`, not this local
increment. Their annotations are retained in
`artifacts/todo-20261023/github-offline-run.txt` and `github-explorer-run.txt`.
No hosted run, release publication or current account billing repair is claimed.

Operation-specific library payload/results and HMI-query contracts remain open.
The full engineering release, native preservation/runtime acceptance and
physical-device qualification remain separate gates. These checks started no
TIA Portal or PLCSIM process and grant no new native operation support.
