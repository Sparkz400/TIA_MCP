# Pre-release security review, 30 October 2026

Reviewed revision: `origin/main` at `ce6285ba`. Only source was read and offline
package checks were run. TIA Portal, PLCSIM, the Host and the workers were not
started. Severities: Critical, High, Medium, Low, Info.

**Result:** nothing Critical or High. One Medium (M1). Before 1.0, fix M1 or
correct its documentation, and add the supported-versions statement that
`docs/TODO.md` asks for (P1). The other findings can ship.

## 1. Dependency vulnerabilities

The checks ran on 2026-10-30 at 07:46 +01:00 on Windows 11, with .NET SDK
10.0.403, Node v24.22.1 and npm 11.19.1. The only NuGet source is nuget.org
(`NuGet.Config`).

| Command | Result |
| --- | --- |
| `dotnet restore TiaMcp.slnx` | Exit 0 |
| `dotnet list TiaMcp.slnx package --vulnerable --include-transitive` | All 61 projects in the solution report "no vulnerable packages" |
| The same `restore` and `list` on `scripts/native/EncoderSeeder` and `tests/TiaMcp.Graph.Tests/Native` | No vulnerable packages |
| `npm audit` and `npm audit --omit=dev` (repository root) | 0 vulnerabilities: 1 prod and 5 dev dependencies (`@playwright/test`, `@types/node` and their transitive packages) |
| `npm audit` in `ui/project-explorer` | `ENOLOCK`. The package declares no dependencies, so there is nothing to audit. |

Of the 31 tracked projects outside the solution, the qualification tools in
`scripts/native` and the retained fixture copies reference only
`Newtonsoft.Json` 13.0.3 or 13.0.4. Both are newer than 13.0.1, the fix for the
only advisory on that package (GHSA-5crp-9r3c-p9vr). None of these projects is
shipped. The Python test requirements (`scripts/requirements-test.txt`, CI and
qualification only) were not audited because pip-audit is not installed.

## 2. Trust boundaries changed on 29-30 October

| Change | Who can trigger it | What bounds it | What is logged |
| --- | --- | --- | --- |
| Force-table edits (`WatchTableDispatcher.cs:438`) | Any MCP caller: `watch_table_prepare` with `forceTable=true` (`WatchTableTools.cs:42`), then `watch_table_apply` (`:70`) with `operationId` and `planSha256` | Offline document edits only. The edit runs on the worker's own scratch project and refuses an online project. `ForceIntention` can never be set, and documents with forcing rows are refused. Nothing is downloaded. | Review, before/after XML and checkpoint. No secrets. |
| `watch_table_monitor` (`WatchTableMonitor.cs:15-22`) | Any MCP caller | Ownership is checked first (`sim_identity`). Local Softbus only. At most 64 rows × 20 samples, at most about 95 s per call. There is no write or force path. | Row fields and values. No secrets. |
| `trustOwnedSimulationCertificate` (`EngineeringTools.cs:43-46`) | MCP caller boolean on `plc_station_upload` | DPAPI ticket valid for 15 minutes, exactly one owned instance, one discovered endpoint at its own IP. The trust is rechecked at the TLS challenge. Nothing is persisted by the adapter. | Only the public certificate hash. |
| `trustOwnedEmptyCpuCertificate` (`EngineeringTools.cs:19-23`) | MCP caller boolean | Same ownership checks. Never certificate-less (`OpennessSession.SimulationProvision.cs:557`). | Only the public certificate hash. |
| Runtime-user login (`OpennessSession.SimulationOnline.cs:502-511`) | MCP caller, through `runtimeCredentialMetadataPath` | Bound to the instance's own download metadata. The vault login runs only in qualification (`scripts/qualify_runtime_plcsim_tcpip.py`, through `--credential-store` on stdin). | Username only. The password is never returned, logged or put on a command line. |
| Credential route (`CredentialRoute.cs`) | MCP caller, through the vault reference in a block-protection plan | The binding is derived by the worker and checked against the hash. Envelopes are sealed to one lease, change and preview. | The secret appears in no response, journal or log. |
| Vault turn-taking (`CredentialVault.cs:190-223`) | Every vault user inside one Host, plus the credential CLI | Per-user folder with an owner-only ACL and an in-process semaphore. No global mutex, so another user cannot squat the lock. | Only `CredentialStoreBusy`. |
| Combined library apply (`HostOptions.cs:18`) | Any MCP caller on every Host, on by default | Plan, `previewHash`, `idempotencyKey` and checkpoint, under the shared-mutation lease. See M1. | Journal and checkpoints. |

Credential route cross-check: the five Low items under "Credential route
hardening" in `docs/TODO.md` are all still accurate. Nothing Medium or higher
was found.

## Findings

| # | Sev | Evidence | Scenario | Suggested fix |
| --- | --- | --- | --- | --- |
| M1 | Medium | `PlcComponentQualificationGate.cs:45-60`; `OpennessSession.PlcComponentCoordinator.cs:55`; `plc-library-integration-recipes.md:115,119` | The default-on apply checks neither the project languages nor instance-profile coverage before the claim. On a production project with another language, or a callee the instance profiles do not cover, the import is applied and saved first. `InstanceCreationProfileUnavailable` only comes at the second step, which leaves `recovery_required`. The guide says other languages "stay refused". | Check the languages and resolve the instance profile in `PlcCombinedApplyRefusal` before the claim, or correct the guide. |
| L1 | Low | `HostOptions.cs:18,46,67`; `Program.cs:20` | There is no opt-out. `--enable-library-integrate-apply` is a no-op. | Add `--disable-library-integrate-apply`. |
| L2 | Low | `OpennessSession.SimulationOnline.cs:545-547`; `SimulationTlsPin.cs:30` | If a certificate-less challenge was trusted, a later challenge that offers a certificate is pinned after the login, because `_answered && !_trusted` is false. This contradicts the intent of `335df2a9`. | Latch `UnauthenticatedTls` and refuse later certificate challenges. Add a unit test. |
| L3 | Low | `SimulationTlsPin.cs:30` | Any challenge with zero certificates is accepted, whatever its `VerificationInfo` or initial selection. | Also require the observed `NonVerified` selection, or reword `simulation.md`. |
| L4 | Low | `OpcUaRuntimeSessionStore.cs:205-208`; `CredentialVault.cs:192,206,218` | The vault lock is held during OPC UA and Web API network calls of up to 10 s. The wait is 5 s and blocks threads, so other vault users (block protection) get `CredentialStoreBusy`. | Check the credential's state under the lock, then release it before the network call. |
| L5 | Low | `CredentialRoute.cs:85`; `CredentialVault.cs:51-57,182-184` | A caller-chosen vault ID creates an empty vault folder and header. This is clutter only, inside the user's own profile. | Make consumer paths refuse a missing vault. |
| L6 | Low | `WatchTableMonitor.cs:22`; `WatchTableDispatcher.cs:59` | Each monitor call runs a full `watch_read` export under TIA exclusive access, into a new folder that is never pruned. | Export only the target table, or prune the folders. |
| L7 | Low | `OpennessSession.TranslationImport.cs:291` | Translation "reopen" adds the operator-registered fixture to the watch owned-scratch set. That is wider than `watch-tables.md:20`. | Use a separate owned set, or update the guide. |
| I1 | Info | `WatchTableTools.cs:43` | The tool description still calls comment, clone and delete harness-only, which is no longer true. | Refresh the text and its bindings. |
| I2 | Info | `AuthenticationInstructions.cs:8,10` | The TLS and login opt-ins are caller booleans. "Explicit user authorization" exists only as instruction text. TIA reuses a login inside one Portal (`fd668cc4`), so "only with `runtimeCredentialMetadataPath`" overstates the protection. | Add a Host option if a hard gate is wanted, and adjust the text. |
| I3 | Info | `OpennessSession.ScratchRuntimeUser.cs:47-63`; `CredentialVault.cs` | Scratch `.dpapi` files and retired vault entries are never deleted. They are DPAPI CurrentUser data. | Document this, or prune them. |
| I4 | Info | `.github/workflows/*.yml` | Actions are pinned to major tags, not SHAs. All of them are first-party `actions/*` and Dependabot watches them. | Optional: pin SHAs. |

## 3. Process

- **Secrets:** a pattern scan covered the tracked tree and all 1,167 commits on all refs. It looked for cloud, GitHub, npm and Slack tokens, private-key headers, key and certificate file types, and password/secret/token assignments.
  - No real secret was found, and no key or certificate file was ever tracked.
  - Three private-key header hits are a refusal sentinel in `scripts/test_public_certificate_host.py:120`.
  - All password/token assignment hits are synthetic fixtures.
- **Workflows:** all three workflows have a top-level `permissions:` block. `offline` and `playwright` use `contents: read`. `wiki` uses `contents: write`, which it needs to push the wiki.
  - There is no `pull_request_target` and no `workflow_run`, and no secret other than `github.token` is used.
  - Event fields reach shell steps only through `env`.
  - Nothing needed fixing.
- **SECURITY.md:**
  - Fixed: it said native credential consumers were unqualified. Block protection was qualified on 30 October (`local.block_protection.20261030`).
  - **P1 (open):** there is no supported-versions statement. `docs/TODO.md` lists it as release acceptance work, and it needs a maintainer decision.
