# Releasing

TIA MCP stays pre-1.0 until the [TODO](../TODO.md) is closed; until then every release is a
0.x pre-release. A version tag builds a **draft** GitHub release; only the maintainer publishes it.

## Versions

- **Product version:** `<Version>` in [`Directory.Build.props`](../../Directory.Build.props),
  [Semantic Versioning](https://semver.org/). It flows into every assembly, `serverInfo.version`,
  the bundle name and the bundle's `manifest.json` (`productVersion`). The copies that cannot read
  it (both `package.json` files, `package-lock.json`, the Add-In's `AddIn.Publisher.xml` and
  `SelectionHandoff.cs`) are kept in step by `scripts/test_product_version.py`.
- **Feature catalog version:** `catalogVersion` in the feature catalog is the data contract
  version and changes on its own; the Host advertises it as
  `capabilities.experimental["tiaMcp/featureCatalog"].version` ([MCP surface](../reference/mcp-surface.md#version)).

## Steps

1. On a branch, set `<Version>` and its copies, then run
   `python -B -m unittest discover -s scripts -p "test_product_version.py"`.
2. In [CHANGELOG.md](../../CHANGELOG.md), rename `## [Unreleased]` to `## [X.Y.Z] - YYYY-MM-DD`,
   state the exact limitations, and open a new empty `## [Unreleased]` above it.
3. Merge the pull request into `main` with the offline checks green.
4. Tag that `main` commit and push the tag:

   ```powershell
   git tag -a vX.Y.Z -m "TIA MCP X.Y.Z"
   git push origin vX.Y.Z
   ```

5. [`release.yml`](../../.github/workflows/release.yml) reruns the offline and Explorer
   workflows, builds the offline bundle with `scripts/Package.ps1 -Offline`, verifies it with
   `scripts/Verify-TiaMcpBundle.ps1 -Strict`, refuses a tag that differs from the bundle's
   `productVersion` or a CHANGELOG without its section, and creates a draft release (marked
   pre-release before 1.0) with the ZIP, its `.zip.sha256` and the manifest, using the CHANGELOG
   section as notes.
6. Review the draft: download the ZIP, compare its SHA-256, expand it and run
   `scripts/Verify-TiaMcpBundle.ps1 -BundlePath <folder> -Strict`, then publish it on GitHub.

If the workflow fails, fix `main`, delete the tag (`git push --delete origin vX.Y.Z` and
`git tag -d vX.Y.Z`) and any draft it left, and tag again.

## What a release contains

Only the portable offline bundle: hosted runners have no Siemens SDK, so the engineering bundle
with native workers is built with `scripts/Package.ps1` on a TIA V21 workstation
([deployment](deployment.md)). The manifest is a hash list, not a publisher signature. Supported
versions are stated in the [security policy](../../SECURITY.md#supported-versions).
