# Dependency updates — 18 October 2026

The nine original Dependabot PR commits are retained as ancestors of the local
integration head `4353fe773722fa95440991005eb9240abd319fdd`. The combined change
touches only both workflow files, both root npm manifests and
`scripts/requirements-test.txt`. No application or native adapter code changed.

## Reviewed changes

| PR | Change | Review result |
| --- | --- | --- |
| [1](https://github.com/Sparkz400/TIA_MCP/pull/1) | upload-artifact v4 → v7 | Existing multi-file uploads retain the default zipped archive mode; artifact names, paths, retention and hidden-file handling are unchanged. |
| [2](https://github.com/Sparkz400/TIA_MCP/pull/2) | setup-dotnet v4 → v6 | Both installed SDK inputs remain supported. `global.json` contains concrete SDK version `10.0.100` with `latestFeature`; the separate `8.0.x` action input remains unchanged. |
| [3](https://github.com/Sparkz400/TIA_MCP/pull/3) | checkout v4 → v7 | The workflow uses ordinary push/PR/manual events, not privileged `pull_request_target` or `workflow_run`. It does not inspect checkout credential-file locations or use authenticated Docker container actions. |
| [4](https://github.com/Sparkz400/TIA_MCP/pull/4) | Playwright 1.64.0 → 1.64.1 | Only the test package and its exact Playwright dependencies change. Clean npm installation and actual Chromium fixture tests pass. |
| [5](https://github.com/Sparkz400/TIA_MCP/pull/5) | referencing lower bound 0.35 → 0.37.0 | This version was already installed; the upper bound remains `<1`. Full Python checks pass. |
| [6](https://github.com/Sparkz400/TIA_MCP/pull/6) | setup-node v4 → v7 | Explicit Node 24 and npm caching inputs remain supported. The workflow does not use removed `always-auth` or `registry-url`/dummy-token behavior. |
| [7](https://github.com/Sparkz400/TIA_MCP/pull/7) | jsonschema lower bound 4.23 → 4.26.0 | This version was already installed; the upper bound remains `<5`. Full Python checks pass. |
| [8](https://github.com/Sparkz400/TIA_MCP/pull/8) | setup-python v5 → v7 | Explicit Python 3.13 remains supported. Removed `pip-install` is not used; the explicit requirements installation remains. |
| [9](https://github.com/Sparkz400/TIA_MCP/pull/9) | psutil lower bound 7.1 → 7.2.2 | This version was already installed; the upper bound remains `<8`. Lifecycle/recovery tests pass in the full Python run. |

The Actions use Node 24 and document minimum runner version 2.327.1. These
workflows target GitHub-hosted Windows/Ubuntu runners. Their actual execution
remains unverified while account billing prevents jobs from starting. Static
input compatibility is not a hosted integration pass.

Overlapping PRs conflicted on adjacent version lines. Resolutions were checked
against the previous workflow bodies: only the five reviewed action-tag
substitutions changed. All 19 action uses were checked against their retrieved
upstream `action.yml` inputs; triggers, permissions, jobs and scripts remain
unchanged. Each original PR head was verified as an ancestor after integration.

## Executed validation

- `npm ci`: clean installation, six packages audited, no reported vulnerability.
- `python -m pip check`: no broken requirements.
- `scripts/Test-Offline.ps1 -Suite Python`: contracts, source bindings and
  retained-evidence inventory pass; **1,480 tests pass in 194.889 seconds**,
  with one existing missing-native-evidence skip.
- Explorer model/client suite: **24 checks pass**.
- Explorer browser fixture suite: **36 checks pass**, twelve screenshots and
  three performance cases; no page errors or network requests. Executed with
  Playwright 1.64.1, Chromium 156.0.8078.4 and Node 24.20.0.
- The 2,172-file source manifest stayed unchanged during these checks; its
  SHA-256 is `67b2dd72c3f580c212bf2812fb2673676ff8f38f57daf78437a851378f0e631a`.

Evidence is under `artifacts/dependency-review-01/`: `verification.json`,
`tested-source.json`, `configuration-validation.json`, the original PR JSON and
diff files, upstream action metadata/release notes, Python results and browser
receipts. Resolved upstream action commit IDs are recorded there; workflows
continue using the reviewed major tags. Only documentation is added after the
tested integration head. Final remote PR/branch state and exact-head CI results
are recorded in `integration.json` after push.

Managed and package suites were not repeated: application source, .NET
dependencies, package scripts and runtimes are unchanged. The browser fixture
does not establish native rendering or live MCP/native interoperability.
No Siemens-dependent compilation or qualification ran. Native NuGet changes
from any subsequent Dependabot PR need separate review and validation.

GitHub subsequently confirmed PRs 1–9 as merged. All nine original remote
branches were already absent; their temporary local review refs were removed
after verifying that each original head remains an ancestor of `main`. Only the
primary checkout is retained.

Follow-up [PR 10](https://github.com/Sparkz400/TIA_MCP/pull/10), first reviewed at
`1e6aab8283e07374c3617686814cd744516039c0`, updates Newtonsoft.Json 13.0.3 to
13.0.4 across 29 native harness/worker and test projects. It remained open at
this earlier checkpoint; the later [dependency validation](newtonsoft-update-20261018.md)
records its separate tests, Framework fixture repair and integration receipt.
Its portable tests can be evaluated here; actual affected Siemens builds and
runtime qualification cannot be established on this machine. This increment's
results do not cover that dependency update. Recheck its head before testing.

## Primary references

- [Checkout v7](https://github.com/actions/checkout/tree/v7).
- [setup-dotnet v6](https://github.com/actions/setup-dotnet/tree/v6).
- [setup-node v7](https://github.com/actions/setup-node/tree/v7).
- [setup-python v7](https://github.com/actions/setup-python/tree/v7).
- [upload-artifact v7](https://github.com/actions/upload-artifact/tree/v7).
- [Playwright 1.64.1 fixes](https://github.com/microsoft/playwright/releases/tag/v1.64.1).

The full 44-workstream goal stays active. Next: the feasible checks for PR 10,
then C02 exact AML/connection/drive/TO
mapping contracts and C03 security/program/runtime identity handoff, while
retaining every component's qualification and unresolved native gates.
