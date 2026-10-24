# Trusted reconciliation of failed provisioning — 24 October 2026

`simulation_provision_reconcile` releases an `outcomeUnknown`
`simulation_provision_scratch_credential` admission only on fresh native evidence.
Otherwise the reservation stays held and the tool returns why.

## Rule

In an empty worker-owned TIA instance (a different Portal process from the one
that provisioned), all of these must hold:

- The admission's scope is the provisioning scope: owned simulator storage, the
  scratch project and, optionally, the discovery observer project.
- Exactly one worker report in the storage belongs to this admission's scope hash.
- The pre-provision archive named in that report still has its recorded SHA-256.
- The archive, retrieved into a new directory, has the same location-independent
  `EngineeringFingerprint` as the current scratch project. This is the existing
  policy-2 project fingerprint without the project path, timestamp and save state.
- Every memory card file matches the original generated-card manifest and its
  program revision.
- The observer project, if declared, exists and contains no device.
- The worker reports that it invoked no native mutation.

Each resource is then reconciled with its own evidence file under
`artifacts/provision-reconciliation/<admission-id>/`. The scratch project is
`unchanged`; storage and observer are `verifiedApplied`. Nothing is powered,
connected, provisioned, saved or replayed.

## Evidence

- Managed: `ProvisionReconciliationChecks` (25 checks) covers each refusal reason
  (project, card, archive, observer, same process, other scope, mutation claimed).
  It also checks binding to the admission's own report, the release, re-admission
  of the released resources, and refusal of unsupported scopes or a second release.
- Native: the admission `9547a9a83cfc42b388fbcab53c1204cb`, left by a failed
  attempt earlier today, was released by `scripts/reconcile_provision.py`. The
  fresh session matched 87 card files, the archive hash, and equal fingerprints
  over 8 native block fingerprints; the observer project was empty
  (`evidence/local.sim.reconcile.20261024.json`).

## Limits

Runtime credential state inside the simulator storage is not verified. Only the
release path ran natively; the refusal paths are managed tests. The fingerprint
excludes generated payload bytes and unexposed vendor data.
