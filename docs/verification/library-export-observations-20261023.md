# Library export observations and exact decimal comparison — 23 October 2026

The external library validator now covers export responses and their original
document manifests. Both schemas follow `LibraryQuery` and `FileArtifact` producer
shapes, retain exact file metadata, and refuse malformed hashes, file sizes,
duplicate paths, invalid relative paths and inconsistent media types. Export
directory aliases must agree. A truncated response must contain its full 200-file
prefix; the separate manifest can contain more files within the existing 4 MiB
input bound. Manifest and response directory hashes have different scopes and
are neither equated nor recomputed. Transfer states remain uninterpreted vendor
observations, including failure or unknown values.

Inspection tests now cover every element row shape, project/global export
availability, unavailable native use attributes and the 201-content master-copy
sentinel. Metadata completeness does not establish complete master-copy contents.

Four new negative cases reproduced a consistency defect: JSON float parsing
rounded distinct large decimals and long fractions, and underflowed distinct
small values to zero. The validator now parses decimal tokens exactly and compares
typed structures recursively, retaining object-order independence and array order.
Boolean, integer and decimal values stay distinct; equal decimal values with
different exponent notation compare equal. The existing overflow refusal remains.

## Verification

- `artifacts/todo-decimal-baseline-20261023.log` retains all four failures on the
  original implementation; 18 tests ran. The failed evidence is unchanged.
- `artifacts/todo-library-observations-final-20261023.log`: 36 tests pass,
  including nine export tests, nine inspection tests and 18 change tests.
- `artifacts/todo-library-identity-final-20261023.log`: all 14 identity/lock tests
  pass with the expanded validator.
- `artifacts/todo-20261023/library-replay.json`: 32 original change views, 32
  previews, 26 uses responses, two export responses, three identity views and
  16 original lock receipts pass. The parent native report remains pinned to
  `2b57f4e7be57321d0439068559b8dd53b47aa989c455d278a0aaaf5eb49b4880`
  and its original bytes are unchanged. Report substructures are derived
  observations, not new standalone native exports.
- `artifacts/todo-export-manifest-01-20261023.json` and
  `artifacts/todo-export-manifest-02-20261023.json`: both original export manifests
  pass actual CLI validation against their retained SHA-256 pins. Their hashes
  are respectively `be1245782369eca460e183cb80a16123ac7cfdb18672a401fa316c5829825b74`
  and `b42aba5c097d814043f7eff2640a58c201286ff73029f5bc27d9de91d3973fbf`.
- `artifacts/todo-20261023/packaged-library-validator.json`: the actual validator
  and schema registry in the relocated immutable package validate the first
  original manifest against its retained pin, without opening listed documents.

The authored test shapes are synthetic. No native software is started by this
work; listed export documents are not read or authenticated. Operation-specific
plan/result payloads, detailed compiler results, HMI library queries and complete
native/runtime qualification remain open.

The first combined offline run stopped at stale source inventory validation.
The refreshed inventory adds seven previously absent observation schema files;
tool, mutation and feature counts remain 261, 56 and 335, with no qualification
promotion. Original failure: `artifacts/todo-20261023/offline-01/result.json`.
Combined regression is recorded separately and is not implied by these focused
results.

The final [combined aggregate and portable package](todo-integration-20261023.md)
now pass, including these changes. Their original failed first attempt remains
retained separately.
