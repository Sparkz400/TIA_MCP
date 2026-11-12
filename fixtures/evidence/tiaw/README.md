# Retained TIAW qualification receipts

Native qualification runs from 15–16 October 2026 wrote their output to the
local short root `D:/TIAW` on the qualification workstation. The verification
records in `docs/` cite those runs by that path. This folder keeps byte-exact
copies of the cited receipts, so the evidence is readable without that disk.

Each run keeps its original relative path: `D:/TIAW/ph1601/admission.json` is
[`ph1601/admission.json`](ph1601/admission.json). [`manifest.json`](manifest.json)
lists the SHA-256 of every copied file, computed from the original bytes.

## What is copied

- The top-level files of every cited run folder: admissions, intents, call and
  step results, reviews, failures, recovery records and console logs.
- `tr1603/continuation-01` and `tr1603/continuation-02`.
- `wt02/engineering-operations`, complete.
- The four `wa1604` observation and source-capture files that
  [`profile-candidate-review.json`](../../watch-tables/native-authoring-v21/profile-candidate-review.json)
  cites by SHA-256: `watch-observations/w_c8a08b4c66af/force-0.xml` and
  `snapshot.json`, and `fb.xml` and `instance.xml` in
  `watch-source-captures/instance-4847a89454564ec0a6fbd1804d7b9e62`. The rest of
  those two folders was never cited.
- The isolated OPC UA receipt
  `iq161/source/artifacts/opcua-offline/a03aca16124c40cc8fed0777a8c0ab2d`
  (its top-level files).

## What is not copied

- `iq161/source` and `iq163/source` are full checkouts of this repository. Only
  their adjacent `source-manifest.json` files are kept; the docs cite them by
  manifest SHA-256.
- TIA Portal scratch projects (`*/s/TiaMcpScratch_*`, `*.ap21`) and other
  nested run folders. They are large, native and were never cited as receipts.
- `iq163/source/artifacts/final-managed-01/result.json` was never written; the
  [revised queue record](../../../docs/verification/revised-queue-integration-20261016.md)
  already states that the rerun was not counted.

The copied files record the original `D:/TIAW/...` and workstation temp paths
as they were observed. Those paths are history, not locations to open.
