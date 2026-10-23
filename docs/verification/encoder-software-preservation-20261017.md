# Encoder software peer preservation — 17 October 2026

## Change and boundaries

The connected encoder qualification now captures an unfiltered, bounded software
inventory for its independently selected PLC before the parameter edit. It retains
native XML exports for every inventoried block, PLC type, tag table and other
technology object. Inventories include software/safety-unit scopes, namespace
presets, available relation metadata and empty block/type/tag/technology user
groups. Added or removed objects, metadata/group changes and changed peer XML
refuse qualification. Protected/unexportable objects cannot silently disappear.

A technology object may also appear in the PLC block inventory. The native adapter
records that relationship using native object equality and complete bounded
membership counts. The selected mapped block must export the same canonical
payload as the selected technology object. Same-name peers are never excluded.
The native equality behavior itself remains unqualified without Siemens.

The only allowed selected-object change is the existing explicit fixture edit:
`Sensor.InverseDirection` from false to true, inserting one native XML StartValue.
The independent XML transformation is checked before parameter dispatch and after
readback. Unsupported fixture edits refuse. This does not restrict the public
technology mutation API to this one fixture.

Checks cover edit, recovery, recovered reopen, changed reopen and fresh inspection
copies. The independent auditor reconstructs capture from the original ordered
requests/results and hash-verified artifact bytes. Filtered inventories, interrupted
reads, substituted summaries, missing exports and foreign owners refuse. The
copy-admission packet additionally pins every peer export and rechecks those files
before each copied-project open and after cleanup.

Current producers declare `encoder-software-peers-v1`. Old reports remain
historical diagnostic evidence with `softwarePeersVerified: false`; they cannot
authorize the current copied-inspection workflow. Audits keep
`fullProjectPreservationVerified: false`.

## Verification

Targeted tests use populated synthetic peers, real artifact/project files and the
production qualification/admission/audit functions. They include a same-name peer
block, the selected mapped block, a type, tag table, another technology object,
empty groups, foreign owners, inventory/byte/call tampering, add/remove/renumber,
profile refusal and fresh copied inspections. Native membership tests exercise
identity equality, ambiguity, truncation and 50,000-entry limits without Siemens.

Final frozen Git tree: `6909e2c9649be934a7b4f038112ecc7b3b966b51` (2038 files).
Source manifest (`artifacts/encoder-software-02/source-manifest.json`) SHA-256:
`e9769fa09b48fdd60a26b2ecee41b2f50e2b633828fce37e661720f3c03879d9`.
Source ZIP SHA-256: `5b2014435b692de87080f5fc7a22cbc9fb4a124ceec91412b9c9e3794e624e80`.

The aggregate (`artifacts/encoder-software-02/source/artifacts/checks/result.json`)
passes 41 managed suites, 1,442 Python tests (one unavailable-evidence skip),
24 Explorer model/client checks, contracts/source bindings and all offline MCP
workflows. The general executable passes 226 checks. All 80 encoder methods also
pass under -OO, including ten software-preservation methods with populated peers.
Run: 2026-10-17T15:05:16.4315284Z to 2026-10-17T15:18:25.3803503Z.
Aggregate receipt SHA-256: `7fd3ac2019a4478c6adc13d9f7c77eb69cda13cf3a0c91a7132ded82fb86f8b5`.
Console evidence (`artifacts/encoder-software-02/aggregate-console.log`).

All 24 package stages (`artifacts/encoder-software-02/source/artifacts/package-checks/result.json`)
pass, including exact ZIP/manifest verification and relocated MCP workflows.
Bundle `tia-mcp-offline_20261017_150543_9c2cc5a7` contains 1440 manifest files.
Package receipt SHA-256: `2327efb5e2d035160cead127353366b484009521156fee833e10ad5c3755f135`.
Manifest SHA-256: `c807fb0f08a0dc92c68fb4f29d3103a9561025c123d113eae48e0b02fd4548fc`.
ZIP SHA-256: `114248be76e10bbbbfdd87fed771292f836fde6624d268bf0ac11e2d89a6a067`.
Browser tests were not repeated for this qualification-harness/inventory change.

The source comparison (`artifacts/encoder-software-02/final-source-comparison.json`)
verifies unchanged tested implementation; only five completion documents differ.
All 106 unavailable
historical evidence binding rows remain unchanged. The
integration receipt (`artifacts/encoder-software-02/integration.json`) records
Git/CI and cleanup state.

A retained synthetic scope check (`artifacts/encoder-software-02/next-hardware-scope.json`)
shows that different unrelated CPU settings remain unobserved while these software
checks pass. It is a coverage demonstration, not native corruption evidence.

The first frozen revision exposed a numeric-Boolean admission gap during review:
a synthetic report changing the required `true` profile value to numeric `1`
passed both source and independent audit. Reproduction (`artifacts/encoder-software-01/numeric-boolean-reproduction.json`).
The final revision compares JSON value kinds and rejects integer, float and
string substitutes. Its targeted ten-method preservation suite passes.

## Remaining work

This proves only the exposed software inventory and retained native XML under the
existing canonicalization policy. No whole-project or atomic-snapshot claim is
made. Native system-group structure, external sources, unexposed services and
relationships, and non-exported hardware/project settings need separate coverage.
The next encoder increment must capture and compare all independently inventoried
hardware settings across the same phases; endpoint checks currently cover only
the selected encoder module/channel. Continue the full TO-03–12, SA-01–12 and
44-workstream queue.

No Siemens SDK/product build, native object-equality validation, native export
compatibility, actual preservation/recovery, simulation or physical test ran.
The retained 106 missing historical evidence records remain unchanged.
