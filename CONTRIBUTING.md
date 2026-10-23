# Contributing to TIA MCP

Start with the [README](README.md), [current status](docs/STATUS.md) and
[remaining work](docs/TODO.md). Open an issue before a substantial feature or
architecture change so its target support and acceptance criteria are clear.

## Development setup

Use Windows, PowerShell 7, the .NET SDK selected by `global.json`, Python 3.13 and
Node.js 24. Run commands from the repository root. `npm ci` installs the locked
browser-test dependencies; .NET restores packages through `NuGet.Config`.
Create a project-local Python environment and install the declared test dependencies:

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r scripts/requirements-test.txt
```

These include JSON Schema validation and process-lifecycle helpers. Do not reuse
another project's environment. The portable Host/Core/library projects can build
without Siemens software; building the full solution cannot.

Full engineering builds also require TIA Portal V21 Openness, PLCSIM Advanced V8
and .NET Framework 4.8 reference assemblies. Follow the
[environment diagnostics guide](docs/guides/environment-diagnostics.md).

## Validation

Run the relevant checks for your change and record their results in the PR.
These test projects are executable harnesses, so use the repository scripts
rather than assuming `dotnet test` executes them.

```powershell
# All offline suites; no Siemens installation required.
.\scripts\Test-Offline.ps1

# The same selections used by the separate Python and .NET CI jobs.
.\scripts\Test-Offline.ps1 -Suite Python
.\scripts\Test-Offline.ps1 -Suite Managed

# Build and validate the portable Windows package, including relocation.
.\scripts\Test-OfflinePackage.ps1

# Explorer unit and browser checks.
npm ci
npm run test:explorer
npx playwright install chromium
npm run test:explorer:browser
```

Each offline invocation writes a fresh report under `artifacts/offline-validation/`.
CI uses Windows for Python and .NET and Linux for the explorer browser. It does
not build the Siemens workers or establish native/runtime qualification. The
separate Windows package job builds only the portable Host and validates its ZIP,
external configuration and supplied-document workflows after relocation.
`Test-WorkstationBoundaries.ps1` also needs Siemens reference assemblies, even
though its tests do not launch TIA. See [runner prerequisites](docs/reference/validation-runners.md).

On a configured engineering workstation, build the full solution and run its
worker-dependent regression checks separately:

```powershell
.\scripts\Probe-Environment.ps1
.\scripts\Build.ps1
.\scripts\Test.ps1 -SkipBuild
```

Live engineering and simulator qualification are separate, opt-in procedures.
Follow [conformance fixtures](docs/guides/conformance-fixtures.md) and
[block testing](docs/guides/block-testing.md), use owned scratch projects and
simulator instances, and report the exact versions, target and binary identity.
Do not run unreviewed PR code on a workstation with engineering credentials.

## Changes and pull requests

- Keep changes focused and follow `.editorconfig`; preserve fixture and vendored
  file bytes unless their content is intentionally being updated.
- Add regression coverage for behavior changes. Update public contracts and
  usage docs when the behavior or supported scope changes.
- Explain the problem, resulting behavior, tests run and remaining limitations.
  Distinguish offline, native compilation and runtime evidence.
- Keep secrets, machine-specific configuration and generated output out of Git.
  Use `.env.example` or `*.local.json.example` for sanitized config examples.
  Review project exports and logs before attaching them to issues or PRs.
- Keep Siemens assemblies out of the repository and packages. Retain existing
  third-party licenses and attribution when changing vendored material.

Contributions are submitted under the repository's [Apache-2.0 license](LICENSE).
For security concerns, follow [SECURITY.md](SECURITY.md).
