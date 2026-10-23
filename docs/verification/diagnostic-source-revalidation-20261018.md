# Diagnostic source revalidation — 18 October 2026

Base: `b0eba9f80f8dd9b73625c8db61d0b8e06b7f69ed`. Full frozen aggregate and package
validation pass. Native execution remains unavailable.

## Defect and repair

The original consumer tests accepted newly hashed cases with altered symbols,
types, XML locations, call sites or parameter bindings. Trace preparation trusted
all fourteen controls; watch planning and replanning had partial checks. Hashing
the case verified its bytes without independently verifying its derived evidence.

One shared validator now re-derives project/source inventory, the exact selected
instance including full call/parameter evidence, every scalar declaration and
optional alarm context from the original saved review. Watch planning, manual
trace preparation and replanning call it before publishing a result. Native/live
claims and online addresses remain unavailable. Invalid derived evidence returns
`CaptureMismatch`; intact incomplete evidence retains existing refusal behavior.
Replanning now requires the predecessor's source review as well as the new one.

No schema or serialized record is rewritten. No new tool, dependency, Siemens
adapter or native qualification is introduced. Runtime capture already has its
own source re-resolution checks and is unchanged by this repair. Caller-authored
reasons, hypotheses and automatic-selection explanations are not authenticated
by these declaration checks. Test Suite receipt authenticity, wider diagnostic
composition and exact alarm-to-member associations remain separate work.

## Evidence

Local receipts: `artifacts/diagnostic-source-revalidation-01/`.

- Original-source managed controls fail in
  `artifacts/diagnostic-source-revalidation-before-all.log`; the original 37
  diagnostic groups pass while the new group exposes accepted altered evidence.
- Repaired managed suite: 38/38 groups, including 42 honestly rehashed controls
  across three consumers and unchanged positive cases.
- Focused actual MCP workflow: 42 refusals after restart, no diagnostic artifacts
  published by any refusal, 108 recorded steps and six clean Host lifetimes.
  Workers are deliberately absent.
- Actual prior-producer compatibility: all 15 cases from the previous frozen
  aggregate. Fourteen complete cases pass watch/trace/replan; the incomplete case
  keeps its `InstanceResolutionIncomplete` refusal. All 44 copied original review
  and diagnostic files remain byte-identical. These are prior artifacts, not
  recreated records from current code.

## Remaining work

This is a C04/F02 prerequisite repair. Continue exact executable alarm/member and
parameter association, full component/HMI semantics, runtime pressure/profile
coverage and the original 44-workstream ledger. No Siemens SDK build, TIA/PLCSIM
execution, real equipment association or native acquisition has been performed.

## Final validation

Two identical 2,452-file frozen snapshots pass all 44 managed suites, 1,525 Python
tests (one existing unavailable-evidence skip), 25 Explorer model/client tests and
all 38 package stages. Both diagnostic MCP runs pass 108 recorded steps, including
42 rehashed alteration refusals across three consumers, six clean Host lifetimes
and no native workers. The managed diagnostic suite passes 38/38 groups.

The relocated package also passes the 15 actual prior-case compatibility checks;
all 44 original retained files remain unchanged. The package contains 1,766 files;
its strict manifest hash is
`b45d58979c28719c5c01169d4d22cd5d1d27ddba31186222363360b7e89fee90`.
Both source manifests have SHA-256
`5c057c54717130b58387a3446c7b2717f1783a73145deb0eef0f075ee6fd15d8`.

All 112 historical evidence entries and their hashes remain unchanged; 106 are
still unavailable. No native qualification flags or original 44-workstream audit
artifacts changed. Final status/verification prose is the only post-freeze source
difference. Hosted CI and integration are recorded separately in the local
`integration.json`; local passing checks do not establish hosted or native success.
