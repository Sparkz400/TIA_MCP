# Block protection through the Host credential route — 30 October 2026

`block_protect`, `block_unprotect` and `block_write_protection_set` used to refuse with `CredentialRouteUnavailable`.
The [change apply credential route](../reference/protected-credentials.md#change-apply-credential-route) now hands a
vault secret to the one admitted apply, sealed to that lease, change, slot and reference. The
[runner](../../scripts/qualify_block_protection.py) generates every password and passes it only through the Host
credential CLI's stdin into an isolated named vault.

## Runs

| Rows | Runner | Build | Result | Evidence |
| --- | --- | --- | --- | --- |
| `change.block_protect`, `change.block_unprotect`, `change.block_write_protection_set` | [`qualify_block_protection.py`](../../scripts/qualify_block_protection.py) | `artifacts/bp2` (`b5fecd8`) | Passed, 38/38 steps | `local.block_protection.20261030` |
| same | same | `artifacts/bp1` (`5a8a947`) | Stopped at the first protect; not evidence | `artifacts/q/bp/bp_da34915a` |
| same, rerun after the route hardening | same | `artifacts/cr1` (`1f11b01`) | Passed, 38/38 steps, same observations | `local.block_protection.rerun.20261030` |

The rerun ran the hardened route: length-prefixed seal entropy, pinned plaintext buffers, the worker half in
`RoutedCredentials` (now tested offline) and the Host's check that a binding names a V21 resource of its own purpose.

One owned TIA Portal V21 instance and one owned scratch project (CPU 1511-1 PN), built and run from a frozen
`git archive` copy; nothing touched a user project, a PLC or PLCSIM.

The first run routed the secret into `PlcBlockProtectionProvider.Protect`, which threw
`EngineeringPasswordPolicyViolationException`. V21 requires 8 to 120 characters with a digit, a special character, and
upper and lower case, and checks this only in the native call, after the checkpoint. The worker now reports it as
`CredentialPolicyViolation`, and the runner generates compliant passwords and provokes the refusal on purpose.

## Observed

| Step | Outcome |
| --- | --- |
| Reference bound to another block, missing record, retired reference, non-UTF-8 secret, vault lock held | `CredentialContextMismatch` (with the expected binding), `CredentialUnavailable`, `CredentialRetired`, `CredentialFormatInvalid`, `CredentialStoreBusy`; before admission, change still `prepared` with no stage, block unchanged |
| Password that violates TIA's policy | `CredentialPolicyViolation` after the checkpoint; `recovery_required`, block unprotected |
| `block_protect` FC, then `block_unprotect` | Applied with checkpoint, compile and save; `IsKnowHowProtected` true, then false |
| `block_unprotect` with a correctly bound wrong password | Native "The password used was rejected", reported as `EngineeringError`; `recovery_required`, block still protected |
| Restore the unprotect checkpoint | Block protected again; the same credential reference unprotects it in the restored project |
| Restore a protect checkpoint | Block unprotected |
| Write protection on the FB: `define`, `enable`, `change` to a second credential | `define` sets `IsDefined` but does not enable; `enable` sets `IsProtected` |
| `disable` with the replaced password, then with the new one | Native "Write-protection could not be set" (`recovery_required`, still enabled), then disabled |
| Restore the disable checkpoint | Write protection defined and enabled again |
| Secret scan | None of the 6 generated passwords (UTF-8, UTF-16 or base64) in the 166 run files (journals, admissions, responses, checkpoints, TIA project), the report, the tool responses or the Host log |

No admission stayed unresolved: every native failure returned and was recorded as a returned outcome.

## Not covered

Software units, namespaces, LAD/FBD, OBs and safety blocks; non-ASCII passwords and the length bound; another
Windows user or profile; a fresh Portal reopen after the changes; `CredentialAdmissionRequired`, the worker's
`CredentialRequired` and its slot-set checks, which are covered offline only (`TiaMcp.Credentials.Tests`). A wrong password is not yet a typed
refusal.
