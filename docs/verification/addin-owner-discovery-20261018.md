# Add-In owner discovery without worker startup — 18 October 2026

Baseline: `dd41789990dec20d2742aab66b54070139c980f5`. Receiver integration inspection found that the private `addin_owner_context` request still passed through ordinary lazy worker startup. An owner lookup on an unused Host could therefore create a worker. The native context implementation alone did not enforce the intended discovery boundary.

## Correction and evidence

`BridgeClient` now refuses an owner observation with `AddInOwnerUnavailable` when no pipe exists, under its existing serialized gate and before startup. This refusal does not fault the bridge. Explicit work can subsequently establish the connection; an observation queued behind that work uses the existing worker and still verifies its peer. Disabled, cancelled, disposed, exited and faulted states keep their existing behavior. No native attach/start, public tool or listener was added.

- The previous production bridge fails the new idle-owner regression: it launches the real fake-worker executable and returns instead of refusing. The original receipt is retained in `artifacts/addin-handoff-01/before.log`.
- All 21 focused bridge checks pass after correction. The new checks exercise the same `WatchTableDispatcher` used by Host, eight concurrent idle observations, no worker events or journal files, later explicit startup, connected observations, exit, terminal refusal, cancellation, disabled workers and disposal. The worker protocol is synthetic; process and pipe I/O are real.
- The first post-fix run exposed an incorrect test assumption that the dispatcher creates no directories. Its journal constructors create empty directories. The final assertion checks that no journal files are written; known empty test directories are removed non-recursively.
- The frozen full general managed suite passes 233/233 checks. Host builds with zero warnings/errors; the actual stdio Add-In selection workflow passes 29 checks over two Host lifetimes. All 30 binding tests, source-binding validation and contract coverage pass at 222 tools, 56 mutation kinds and 296 features.
- All 106 missing historical evidence entries retain their original metadata and hashes. `artifacts/addin-handoff-01/verification.json` binds the executed logs. The 2,155-file source snapshot receipt has SHA-256 `9a79d375ff68a7dd281bee26803072db29adf6e430df55adb47196765bc6f32f`; only documentation changed afterward.

The full 43-suite/Python aggregate and 29-stage package suite were not repeated for this three-line bridge guard. Their most recent full result belongs to the baseline commit, as recorded in [owner-context verification](addin-owner-20261018.md). Native worker compilation, TIA attachment and actual companion behavior remain unverified.

## Receiver and enrollment remain open

The full AI-03 receiver, discovery, enrollment, one-use challenges and durable acknowledgment/recovery are still required. This correction closes an ownership bug found while preparing that work; it is not a transport implementation.

Context7 resolved only the V20 Openness index and returned no matching documentation for external verification of an active Add-In. The retained member-name search also does not establish a public verification route; member-name absence is not proof that none exists. Siemens documents [activation in the Add-Ins task card](https://docs.tia.siemens.cloud/r/en-us/v21/introduction-to-the-tia-portal/extending-tia-portal-functions-with-add-ins/enabling-and-disabling-add-ins) and [package certificate display](https://docs.tia.siemens.cloud/r/en-us/v21/introduction-to-the-tia-portal/extending-tia-portal-functions-with-add-ins/displaying-certificates). Those UI procedures do not specify an external loaded-producer attestation protocol. A matching OS process, supplied hash or returned nonce must not be substituted for that missing proof.

Next: resolve the trusted producer enrollment boundary while implementing the bounded challenge/receipt flow against explicit test authorities. Keep production admission closed until its authority is established. The complete 44-workstream goal remains active.
