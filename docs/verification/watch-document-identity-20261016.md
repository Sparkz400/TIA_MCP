# Watch document identity and independent preservation — 16 October 2026

This D02/L05/F02 increment repairs two content-identity gaps. It does not introduce
a native mutation profile or qualify a new Siemens export format.

## Defects and repair

The managed semantic hash serialized only the XML root. Prolog/epilog comments
and processing instructions therefore did not affect content identity, although
the document editor retained them. A change to those nodes could evade the
dispatcher freshness check. The hash now includes the complete document, excluding
only document-level formatting whitespace and the existing permitted volatility.
All seven retained genuine watch/force captures keep their previously recorded
semantic hashes. XML encoding/declarations remain outside semantic identity;
no-op documents still retain their exact original bytes.

The base independent workflow oracle had the same outer-node omission in its
candidate, readback and peer comparisons. The exact source-negative verifier also
omitted those nodes when checking a HeaderAuthor-only delta. Those comparisons
now use the existing Expat annotation reader, moved from the expansion verifier
to their shared base with the old expansion entry point retained. Identical
annotations remain supported; additions, losses, edits and position changes are
significant. This is an independent oracle, not a call into the managed hash.

The oracle also normalized whitespace and Created fields by element name alone.
That could hide changes to familiar names nested inside opaque content. Its
normalization now follows the observed document/table/row/comment ancestry.
Opaque payloads do not acquire native normalization merely by using those names.

## Development evidence

All receipts are under `artifacts/watch-identity-dev`:

- `managed-before.log`: original code fails the new annotation identity group
  (35/36 pass). `legacy-hashes.log` records all seven old genuine-capture hashes.
- `python-before.log`: six failing assertions reproduce opaque-container/time
  normalization and outer-annotation loss in the original independent verifier.
- `managed-after.log`: 36/36 groups pass, including exact legacy-hash compatibility,
  no-op bytes, complete-document edit preservation and annotation significance.
- `python-final.log`: 172 watch-related checks pass. Additional adversaries cover
  target readback, table/block peers with equal reported digests and exact source
  deltas. Positive controls retain matching annotations.
- Dispatcher tests change an outer comment or processing instruction between
  review and apply; both must refuse with StaleContent before checkpoint/apply
  and release the acquired lease. These use synthetic bridge observations.

## Frozen validation

The frozen source is `artifacts/watch-identity-01`, based on commit
`71154ca4bf35b956cdcdeb07710393936b68e6e2`. Its staged tree is
`a09278e7a476bb5c237d250dea226bc7e08c40af`, with 1,789 tracked files. The source
manifest SHA-256 is
`295265158ffb25f39333e5dafba1d91b69e247af12d04b86cd8590f2a6296202`.

The full aggregate passes all 29 managed suites, 1,273 Python tests with one
unavailable-native-evidence skip, 24 Explorer model/client checks and all actual
portable MCP workflows. This includes the 36 watch groups, 22 expanded dispatcher
checks, five actual Host watch boundaries, 41 translation admission checks,
72 VCI checks, 43 GRAPH checks and 48 OPC UA checks. Native workers were not used.

- `source/artifacts/checks/result.json` SHA-256: `8b5fe036d842c34156c1f84afe1eaacef3bbd931a4f7d90c482c231c6fa84465`.
- `source/artifacts/aggregate-console.log` SHA-256:
  `e22d00610fafa673593c358fc1efe13f3787df1dafd4c8882beab1fe4f4f35ae`.

All non-documentation files were compared against the frozen source manifest and
match. Later edits are documentation only. Contracts remain 175 declared tools,
55 mutation kinds and 248 features. The evidence inventory still reports six
matching files and 106 unavailable historical files; no claim is promoted.

Watch identity commit `3326931` passes all hosted jobs
(`38930866502`, Explorer `38930866322`). A portable ZIP and browser UI were not rebuilt locally
for this source-only increment.

## Limits

No Siemens product or SDK is installed here. The shared net48 adapter build,
native import/compile/reopen/recovery and real product compatibility remain open.
Documents containing formerly ignored outer annotations now have different
content identities and require fresh review. No retained evidence is rewritten
or promoted, and the full offline execution queue remains active.
