# Retained UA observation reports

`diagnostic_ua_observe` composes an exact retained polling or subscription prefix
with its original [pre-acquisition diagnostic binding](diagnostic-ua-binding.md).
It performs no UA service, starts no native worker and does not replay acquisition.

After a bound capture has published a `snapshotRef`, call:

```json
{"snapshotRef":"<exact runtime_ua_capture_status or runtime_ua_subscription_status snapshotRef>"}
```

The receipt contains the report `id`, artifact path/hash/byte count, binding and
experiment IDs, and acquisition mode. Read the complete JSON with `artifact_read`.
The source may be a running committed prefix or a terminated capture. Later
capture progress never changes the selected prefix or its report. Uncommitted
data and final cleanup remain unknown when the prefix is still running.

The report retains:

- The original plan and every segment byte, with relative paths, SHA-256 hashes
  and base64 content, alongside all decoded rows and the last recorded progress.
- The exact diagnostic binding bytes and saved experiment, selected signal/node
  mappings, caller unit labels, and signals outside the selected observation set.
- Per-signal counts for observed values, Good/Uncertain/Bad quality, nulls,
  unsupported values, metadata errors, host read errors and missing timestamps.
  These dimensions overlap; they are not percentages or mutually exclusive totals.
- Exact first and last observations, including value encoding, original type,
  status, source/server timestamps and client timing. Large integers remain
  decimal strings. No automatic datatype or unit conversion takes place.
- Requested acquisition settings, server-revised subscription/item settings,
  gaps, duplicate envelopes, keepalives, failures, overflow and cleanup evidence.
  Duplicate envelopes remain in the raw rows but are not counted twice as values.

Every hypothesis stays `unresolved`. Independent online signal/program
association, simultaneous PLC samples, synchronized clocks, physical qualification
and source authentication are not established. A retained first/last observation
does not prove continuous state, a transition time, a causal relation or a fix.

## Exports and Explorer

Call `diagnostic_ua_report_export` with an exact report ID and format:

```json
{"reportId":"<diagnostic_ua_observe.id>","format":"html"}
```

Supported formats are `json`, `markdown` and `html`. JSON is byte-identical to the
original report. Readable exports include the complete original report JSON,
selected signals, timing/quality gaps, unresolved hypotheses and source hashes.
HTML is self-contained and script-free; evidence text is escaped in both formats.

In the portable Project Explorer, select **Open capture** and choose the exported
report JSON. Explorer verifies the retained original-file hashes and chain,
binding bytes and independently derived counts before replacing the current view.
It displays exact values, requested/revised settings, timing and cleanup evidence,
and pages through original rows. **Download capture JSON** preserves the supplied
file bytes. These local consistency checks do not authenticate the source.
Existing Web runtime capture imports retain their previous behavior.

## Retention and refusal boundaries

The Host reopens and validates the original chain, binding, experiment and saved
declarations whenever a report is composed or exported. It compares an existing
report with a freshly derived result, so changing a summary and recomputing its
outer hash does not make the report valid. Missing or changed originals refuse;
embedded copies do not replace unavailable source files in the Host.

An original capture without a diagnostic binding cannot be bound retroactively.
Its raw status, pages and exports remain usable. Report composition permits at
most 10,000 complete rows, 3 MiB of original chain bytes and an 8 MiB report/export.
Oversized prefixes refuse without silent truncation; use raw capture exports for
larger captures. A smaller explicit historical prefix may be selected, but it
must not be described as the complete acquisition.

Contracts: [report](../../contracts/diagnostic-ua-observation.v1.schema.json),
[export](../../contracts/diagnostic-ua-observation-export.v1.schema.json).
