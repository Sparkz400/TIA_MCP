# Security policy

## Reporting a vulnerability

Use this repository's **Security → Advisories → Report a vulnerability** option
when private vulnerability reporting is enabled. Include the affected revision,
Windows/TIA/PLCSIM versions, impact and minimal reproduction steps using a
synthetic project. Redact credentials, customer data, project exports and logs.

If the private reporting option is unavailable, open an issue asking the
maintainers for a private reporting channel without disclosing the vulnerability
or exploit details. No dedicated security email or response-time guarantee is
currently published.

## Supported versions

TIA MCP is pre-1.0. Only the latest `main` revision and the newest release are
supported; fixes land on `main` and ship in the next release, never as backports
to older 0.x releases. There is no LTS policy.

| Version | Supported |
| --- | --- |
| `main` and the newest 0.x release | Yes |
| Older 0.x releases | No |

Check that the issue reproduces on current `main`, and include the exact commit
or the bundle's `manifest.json` (`productVersion` and its SHA-256) so maintainers
can identify the affected code.

## Supported scope

Relevant boundaries include MCP input validation, named-pipe and SSH access,
artifact and library paths, project checkpoints, credential handling and owned
simulator lifecycle. Native compilation and simulator results are target-specific;
passing offline CI is not evidence that an engineering change is safe to deploy.

## Handling sensitive material

Use an appropriate protected store for credentials. The [local credential commands](docs/reference/protected-credentials.md) provide current-user DPAPI storage with exact public context and restricted ACLs; native consumers and cross-user recovery remain unqualified. Do not put secrets in MCP arguments, public binding selectors, logs or plaintext configuration.
Ignored files are not encrypted, and ignore rules do not remove previously
committed content. If a credential is exposed, revoke or rotate it first.

Exports, checkpoints and test reports may contain project source and identifiers.
Share sanitized reproductions, retain recovery archives privately, and run live
tests only on owned scratch projects and simulator instances. Consult the
[engineering workflow](docs/guides/engineering-workflows.md) and
[deployment guide](docs/guides/deployment.md) for operational boundaries.
