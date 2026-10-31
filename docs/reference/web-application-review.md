# Supplied web application review

`web_application_review` compares supplied public configuration and the original
ZIP files for one explicitly selected application, before and after an observation
or proposal. `web_application_review_page` reopens the immutable result. Neither
tool requires Siemens software. They do not render pages, execute scripts, contact
endpoints, generate DBs, configure a device or deploy an application.

## Input workflow

1. Import an exact saved project review with `project_snapshot_import`. Select its
   nonvirtual project/hardware `nodeId` and `objectPath`; names are not selectors.
2. Upload each original ZIP through `artifact_upload_begin/chunk/commit`. Supply
   the resulting `artifactRef` and the expected original SHA-256. Extensions in
   upload display names do not select the format: this workflow always checks ZIP.
3. Call `web_application_review` with a direct `request` object using
   `web-application-review-request.v1`. The schema is
   [web-application-review.v1.schema.json](../../contracts/web-application-review.v1.schema.json).
   The request contains `reviewId`, `owner`, `selectedApplicationId`, `afterKind`
   (`observation` or `proposal`), and `before`/`after` phases.

Each phase has a `configuration` and an `archive`. Configuration has explicit
`webEnabled` and `systemPagesEnabled` booleans or unknown nulls,
`applicationInventoryComplete`, an `applications` array and `generationProperties`.
Applications contain `applicationId`, `name`, nullable `applicationType`, nullable
`isDefault` and `properties`. Property rows contain an exact `name` and a scalar or
null `value`. Values remain supplied public declarations; unknown property names,
application types and generation settings receive no target-specific validation.
An empty property list establishes no native property coverage.

For example, an archive selector is:

```json
{
  "uploadRef": "<committed artifactRef>",
  "sha256": "<64 lowercase hex characters>",
  "rootRelative": "site",
  "defaultPage": "index.html",
  "purpose": "userDefinedPageSource",
  "unavailableReason": null
}
```

The root is relative to the outer ZIP, with `""` selecting its root. The default
page is relative to that selected root. Both require exact case and forward-slash
paths; URL decoding, case fallback, absolute paths and traversal are not used.
The default page must be an existing file. Selecting `site` does not include sibling
directories or a separately extracted nested archive. A nested ZIP inside `site`
remains an opaque original file in that selected manifest.

Purpose is either `userDefinedPageSource` or `webApplicationArchive`; neither label
certifies a Siemens format. An unavailable phase uses null `uploadRef`, `sha256`,
`rootRelative` and `defaultPage`, retains an explicit purpose, and supplies a nonempty
`unavailableReason`. An available archive requires the selected application ID to
appear in that phase's configuration. IDs are caller-declared within the selected
owner, not native identity authentication or name matching.

## Native capture with `web_inspect`

With TIA Portal V21 attached, `web_inspect(projectName?, hardwarePath?, maxItems)`
returns, per CPU, an `observation` holding one phase's `configuration` and an
unavailable `archive`. `systemPagesEnabled` comes from the system web pages feature
and `applications` from the default web pages feature, with the native name as
`applicationId` and empty `properties`. `webEnabled` is read only from an exposed
Boolean `WebserverActivate` attribute and is otherwise `null`. `generationProperties`
hold every completely captured scalar web-named CPU attribute, at most 128.

web_inspect reads no page sources, so replace the archive selector with a committed
ZIP selector to compare files, and pick `selectedApplicationId` from `applications`.
It never generates blocks. Legacy web users are reported by `access_inspect`. The
capture is verified (`local.read_contracts.20261028`).

## Results and retained evidence

The receipt links original request, original ZIPs and the complete report.
Upload release and Host restart do not invalidate those independent retained copies.
Reopening verifies original hashes and the exact saved review, re-inspects both ZIPs
using existing PackageStore validation, and recomputes the report. No mutable
extraction cache or released upload is trusted as report evidence.

- `observations`: normalized supplied configuration, exact archive selectors,
  selected file hashes/lengths, default-file digest and excluded/nested file counts.
- `configurationChanges`: web/system-page, inventory-completeness and opaque
  generation-property changes.
- `applicationChanges`: changes by explicit ID. Missing rows are
  `notObservedBefore/After`, never native create/delete instructions.
- `fileChanges`: added/removed/changed/unchanged relative files only when both
  archives are available and their purposes match. Different purposes or unavailable
  bytes keep comparison unknown. Root selection on both sides is explicit.

Application/property row order is normalized; identifiers, values and original bytes
are preserved. Unknown settings/defaults, incomplete inventory, multiple declared
default applications and enabled-page declarations with web serving disabled remain
explicit findings. These are supplied-data contradictions, not effective native
policy or proof that the remaining configuration is valid.

Use `web_application_review_page(recordId, section, offset, count)` for these four
sections, or `summary`. Pages allow 1..200 rows and at most 128 KiB; oversized rows
are explicitly omitted with the full report available through `artifact_read`.
Complete original ZIPs remain downloadable, including unsupported script/binary
content. Do not execute untrusted downloaded material merely because it was reviewed.

## Bounds and qualification

Requests are limited to 1 MiB, 512 application rows per phase, 128 properties per
application/generation list and 8,192 characters per public text value. ZIPs are
limited to 16 MiB each, 64 MiB expanded data, 4,096 intake entries including nested
content and three archive levels. Existing ZIP path, link, CRC, collision and actual
streamed-size checks apply. Nested ZIP validation also applies to ZIP files intended
as downloadable assets; unsupported nested content can therefore refuse the input.
Extraction is serialized, bounded to 60 seconds per phase and removed afterward.
A concurrent analysis waits up to five seconds for the running one, then returns
`IntakeBusy`; it does not start another extraction.

Both phases validate before immutable review publication. Failed transient
extractions are removed; original generic uploads remain until explicit release.
Publication uses content-addressed files, a 16 MiB report limit and a 512 MiB
retention limit including incomplete files. A capacity failure may leave already
published immutable originals; it cannot create a successful partial review.
These are public-data artifacts, not protected credentials or secret sanitization.

`nativeQualified`, `sourceAuthenticityVerified`, `associationVerified`,
`siemensCompatibilityVerified`, `generatedBlocksVerified`, `deploymentVerified` and
`endpointVerified` are always false. App configuration, user-page source, generated
blocks and deployed assets remain separate. Beyond the read-only `web_inspect`
capture, native profile capture, generated DB ownership/collision/preservation,
protected-resource recovery, actual deployment
and independent browser/endpoint acceptance remain required in the
[full D12/X03 plan](../plans/expansion-plans/web-server-access-configuration.md).
