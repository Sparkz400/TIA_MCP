# CPU protection level through the Host credential route — 30 October 2026

The access-level modes of `PlcAccessControlConfiguration` compile in V21 only with a full-access password
([29 October boundary](plcsim-openness-gaps-20261029.md)). `hardware_cpu_parameters` now takes `protectionLevel`
(`ReadAccess`, `HMIAccess` or `NoAccess`) with a credential reference to that password, bound to the PLC path, the CPU
item and the `cpuProtection` purpose; the Host routes it like a block password
([credential route](../reference/protected-credentials.md#change-apply-credential-route)). The
[runner](../../scripts/qualify_cpu_protection.py) generates every password and passes it only through the Host
credential CLI's stdin.

## Runs

| Build | Result | Report |
| --- | --- | --- |
| `artifacts/cr1` (`1f11b01`) | Stopped at the first apply-side prepare after 7 typed prepare refusals: the CPU exposes no `PlcAccessLevelProvider` under `EnabledWithAccessControl` | `artifacts/q/cr1/cp_76d5278c`, not evidence |
| `artifacts/cr2` (`ff895d8`) | Stopped at the first set: V21 refused `set_PlcProtectionAccessLevel` | `artifacts/q/cr2/cp_7dfbc9fe`, not evidence |

One owned TIA Portal V21 instance and one owned scratch project (CPU 1511-1 PN) per run, from a frozen `git archive`
copy; fail-safe access off; nothing touched a user project, a PLC or PLCSIM.

## Observed

| Step | Outcome |
| --- | --- |
| Access-level mode without a level; `FullAccess`; `FullAccessIncludingFailsafe`; a level without a credential; a level under the UMAC mode; a plaintext `password` field; `Disabled` | `InvalidRequest`, `ProtectionDowngradeRefused` (twice), `CredentialInputInvalid`, `InvalidRequest`, `CredentialInputInvalid`, `ProtectionDowngradeRefused` at prepare; nothing changed |
| Credential bound to another CPU, missing record, retired reference | `CredentialContextMismatch` (with the expected binding), `CredentialUnavailable`, `CredentialRetired` before admission; change still `prepared`, protection unchanged |
| `access_inspect` under `EnabledWithAccessControl` | `accessLevel` null: the provider is absent until an access-level mode is written (fixed in `ff895d8`) |
| Mode written, then `PlcProtectionAccessLevel = ReadAccess` | Native "This operation cannot be performed because runtime access is enabled", `EngineeringError` after the checkpoint, `recovery_required` |

## Boundary

V21 refuses the protection level while UMAC runtime access is enabled on the device. Every owned scratch CPU has it
enabled: scratch creation adds a generated runtime user with the Full access right, which PLCSIM downloads need. V21
Openness documents no switch for runtime access (`UmacDevice` exposes only `AvailableDeviceFunctionRights`), and
removing the scratch runtime user is not a reviewed route. The route therefore stays built and offline-tested but is
not natively qualified; setting the level, the level-only change, its checkpoint restore and the change back did not run.
