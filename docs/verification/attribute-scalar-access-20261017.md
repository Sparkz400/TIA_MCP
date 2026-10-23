# Shared attribute scalar access - 17 October 2026

The shared native attribute path guessed string for null values without type evidence,
selected the first of ambiguous supported types, reconverted values after preflight
without fresh access checks, and compared readback without its native type. The new
`OpennessSession.Attributes.cs` contains the production preflight/apply routines.
The existing scalar converter and sensitive-name guard remain unchanged.

## Callers and behavior

The repair reaches hardware attributes, channel attributes, network configuration,
tag edits, user-constant edits and configured I/O addresses. The same allowlists,
selectors, protection/safety exclusions and outer checkpoint/compile/save/recovery
workflow remain in place. Address writes now call the common helper and retain their
independent address/type/length preservation comparison. Channel normalization reuses
the already converted native values instead of independently guessing their types.
Network configuration preflights attributes before subnet connect/disconnect effects,
then rechecks them again when applying after those effects.

Every selected attribute requires one exact-name access declaration. Preview/apply
preflight require read/write access; readback requires read access. A null current
value needs exactly one non-null supported type. All conversions happen before the
first attribute setter. Before each setter, fresh metadata/current type must still
match the converted scalar type. Final readback verifies exact type and value.
Read-only-after-write remains valid. Empty batches do not inspect attributes.

This is not a native transaction. A setter failure, metadata drift or a preceding
write that changes a later attribute's type can leave earlier effects; the helper
does not replay them. Type-changing tag/constant edits may need separate reviewed
changes. Subnet effects may already exist when apply-time revalidation fails.
Unknown access/type evidence now refuses where the older path could proceed.

## Executed and unexecuted checks

[Focused run](../../artifacts/attribute-write-tests-01.log): **163/163** general
executable checks pass, including four new groups compiling the actual production
helper against synthetic `IEngineeringObject` API shapes. They cover absent/null/
duplicate/case-mismatched metadata, read/write permissions and protected/unsupported
names before explicit getters/setters; ambiguous or absent null-value types;
whole-batch conversion/getter/metadata failures before any setter; permission/type/
declared-type drift before dispatch; partial failures with exact setter order and
no replay; readback value/type/access/null/getter failures; read-only-after-write;
scalar roundtrips, explicit null type declarations and invalid conversions.
Channel-style normalization returns the exact converted runtime types.

The six affected catalog entries are unverified for the changed source, with their
historical evidence IDs preserved. All 106 unavailable historical evidence binding
rows/hashes remain unchanged. Tool/mutation/feature totals remain 199/55/272.
Final frozen Git tree: `08052a555ebb23c16e8f55dc123ee04eac019148` (1957 files).
[Source manifest](../../artifacts/attribute-write-01/source-manifest.json) SHA-256:
`d5ed41ef84544df55336dd45277917ec7d190ae9d7a5fee0a75d60b2da424b0f`.
Source ZIP SHA-256: `eded9ded3bdcffbe476474853bf24df630e730412bfd3a3c02925c7d85d2c051`.

The [aggregate](../../artifacts/attribute-write-01/source/artifacts/checks/result.json)
passes 39 managed suites, 1,295 Python tests (one existing unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all offline MCP workflows. The
general executable repeats all 163 checks and the four new attribute groups.
Run: 2026-10-17T05:01:38.1469442Z to 2026-10-17T05:11:43.4582162Z.
Receipt SHA-256: `8ebede6d2960b1ce5949c0187d19a36e4587065d2006ecaa7b601f97d855a48e`.
[Console evidence](../../artifacts/attribute-write-01/aggregate-console.log).

All 22 [package stages](../../artifacts/attribute-write-01/source/artifacts/package-checks/result.json)
pass, including relocation. Bundle `tia-mcp-offline_20261017_050200_9112e19e` contains 1387
manifest files. Receipt SHA-256: `aba1bb22aee7c32427a03c79a415ed3764d50f2f957e47af22917a2a03cd06ea`.
Manifest SHA-256: `d004b783a4a81ecfd8370f43bf32cc43d91d734d4dcf5569ca6b1e0ec572f8c2`.
ZIP SHA-256: `a53d99233ac457cb04f41997caf0117d3dbe28e9f5e0b091b28b531e29615a9b`.
This offline package excludes native workers; it does not compile the Siemens SDK.
Hashes establish retained byte integrity, not publisher or native authenticity.

The [source comparison](../../artifacts/attribute-write-01/final-source-comparison.json)
permits only completion-documentation changes after the frozen tests.
The [integration receipt](../../artifacts/attribute-write-01/integration.json)
records the pushed commit, hosted CI outcome and cleanup.

The tests prove these linked helper branches on .NET 10, not the actual SDK ABI or
the whole native worker. Call-site wiring and outer lifecycle were inspected in
source only. Native getters may perform their own internal reads; no whole-worker
zero-read claim is made. This machine has no Siemens SDK, TIA, PLCSIM or PLC.
Genuine SDK compilation, target metadata/permissions, dependent native types,
compile/save/reopen, address/channel/network preservation and actual checkpoint
recovery remain unexecuted. Retained historical native fixtures do not qualify the
changed implementation.

## Next work

The parameter-read source repair is now implemented and tracked in its
[own verification record](technology-parameter-read-20261017.md). Continue TO-03
capture/metadata, TO-04–12 and the full
[44-workstream queue](../plans/offline-execution-20261016.md). Native evidence cannot
be manufactured by additional synthetic tests. GitHub refused all five jobs for
base `66eb800` before execution because of account payment/spending limits.
