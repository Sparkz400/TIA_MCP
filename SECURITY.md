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

## Supported scope

This project is under active development and has no published LTS support policy.
Reports against the latest development revision are preferred; include the exact
commit or package manifest so maintainers can identify the affected code.

Relevant boundaries include MCP input validation, named-pipe and SSH access,
artifact and library paths, project checkpoints, credential handling and owned
simulator lifecycle. Native compilation and simulator results are target-specific;
passing offline CI is not evidence that an engineering change is safe to deploy.

## Handling sensitive material

Use an appropriate protected store for credentials. The [local credential commands](docs/reference/protected-credentials.md) provide current-user DPAPI storage with exact public context and restricted ACLs. The only native consumer is the [block-protection change route](docs/reference/protected-credentials.md#change-apply-credential-route), qualified on an owned scratch project; cross-user recovery remains unqualified. Do not put secrets in MCP arguments, public binding selectors, logs or plaintext configuration.
Ignored files are not encrypted, and ignore rules do not remove previously
committed content. If a credential is exposed, revoke or rotate it first.

Exports, checkpoints and test reports may contain project source and identifiers.
Share sanitized reproductions, retain recovery archives privately, and run live
tests only on owned scratch projects and simulator instances. Consult the
[engineering workflow](docs/guides/engineering-workflows.md) and
[deployment guide](docs/guides/deployment.md) for operational boundaries.
