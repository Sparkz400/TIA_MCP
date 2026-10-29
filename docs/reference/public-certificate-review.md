# Public certificate inspection and association review

`public_certificate_review` inspects supplied public X.509 certificates without
Siemens installed, then compares declared store/usage associations. It does not
install certificates, authenticate endpoints, inspect private keys or build trust
chains. Source ownership and native assignment are not inferred from certificate
names or fingerprints.

1. Import/reuse a saved review and choose one exact nonvirtual project or hardware
   `nodeId` and `objectPath`. Same names or virtual navigation nodes cannot substitute.
2. Call `public_certificate_review(request)` with a direct
   [public-certificate-review-request.v1 object](../../contracts/public-certificate-review.v1.schema.json).
   Do not use generic artifact uploads for certificate input. Generic uploads retain
   bytes before domain validation; this dedicated operation validates both phases
   before publishing its own retained input.
3. Retain the returned `recordId`, `input` and `reportArtifact`. Reopen with
   `public_certificate_review_page(recordId, section, offset, count)`, using `summary`
   or `observations`. `artifact_read` downloads the complete public request/report.

The request requires `reviewId`, `owner`, `evaluatedAtUtc`, `afterKind` and `before`/
`after`. UTC time has the exact form `2026-10-17T00:00:00Z`, including seconds and Z;
there is no implicit current-time lookup. `afterKind` is `observation` or `proposal`;
neither authorizes a certificate operation.

Each phase contains:

```json
{
  "association": {
    "storeId": "caller-supplied-local-store-id",
    "certificateId": "caller-supplied-native-id",
    "usage": "webServer"
  },
  "format": "der",
  "contentBase64": "<base64 of the public DER file>",
  "expectedSha256": null,
  "unavailableReason": null
}
```

`storeId` and `certificateId` may be null for unobserved identities. `usage` is a
supplied label: `webServer`, `opcUaServer`, `opcUaClient`, `tls` or `unspecified`;
it is not a Siemens enum/setter or proof of permitted usage. To retain an unavailable
certificate, set `format` and `contentBase64` to null and provide a nonempty
`unavailableReason`. Do not manufacture a certificate or native ID to fill a gap.

For available content, use canonical base64 of exactly one DER certificate or a
UTF-8 PEM containing exactly one `CERTIFICATE` block. Only ASCII whitespace may
surround the PEM block. Multiple certificates, trailing content, other PEM labels,
PFX/PKCS12/PKCS7/key containers and extra fields in known ASN.1 wrappers refuse.
Duplicate extension OIDs and malformed recognized extension structures also refuse.
The parser is not a complete RFC policy validator: unknown extension values and
unsupported GeneralName choices remain opaque public bytes, and signatures are
not verified. Public-looking identifiers/extensions are not a secret detector.

The report includes SHA-256 of the original certificate input and of the parsed
public DER, original subject/issuer name encodings, display names, serial, algorithm
OIDs/public-key encoding, validity bounds and ordered extensions. SAN DNS/IP/email/
URI, key-usage bits, EKU OIDs and basic constraints are decoded; unknown data and
critical flags are preserved. It retains missing algorithm parameters as null.

Validity is `notYetValid`, `withinInterval`, `expired` or `invalidInterval` at the
explicit evaluation time. Matching a caller-supplied `expectedSha256` is only byte
comparison; mismatches remain findings. Equal DER certificates with different PEM
formatting retain the same public identity and distinct original-input hashes.
Missing bytes produce unknown comparison, not equality or safe removal. Association
changes are changes in declarations, not evidence of actual assignment or deployment.

## Native capture with `certificate_inspect`

With TIA Portal V21 attached, `certificate_inspect(projectName?, hardwarePath?, maxItems)`
lists each CPU's local certificate store. Per certificate it reports the native ID,
subject common name and `hasPrivateKey` flag, plus subject, issuer, serial, SHA-1
thumbprint, SHA-256 and `notBeforeUtc`/`notAfterUtc` read from the exported public
certificate. Its `observation` is one phase of this request: re-encoded public DER,
`storeId` `<cpu path>/certificates`, the native certificate ID and
`usage: "unspecified"`, because V21 exposes no per-certificate usage or web server/
OPC UA assignment. An export that is not exactly one public certificate becomes an
unavailable phase with a reason. Private keys are never exported, and the temporary
export file is deleted.

The result also lists dynamic certificate-management services and certificate- or
syslog-named CPU attributes as the exposed assignment evidence, the global-store
enable flag and the project syslog servers. Supply the saved owner and
`evaluatedAtUtc` yourself. The capture is verified (`local.read_contracts.20261028`).

Bounds: 384 KiB request JSON, 96 KiB decoded input/phase, 64 KiB DER/phase, 128
extensions, 512 SAN entries and 128 EKU OIDs. Reports are limited to 16 MiB; retained
storage to 512 MiB including unfinished files. Pages have at most 200 rows and
128 KiB; oversized rows explicitly require downloading the full artifact. Reopening
revalidates original public input, exact saved review and deterministic report.

Send public certificates only. Invalid key/container/extra-payload requests do not
publish operation-owned input or report artifacts. Client/MCP request logging is
outside this operation's control; do not submit secrets expecting them to be erased.
The tool never requests private-key availability, accesses a certificate store,
builds a chain, fetches revocation/AIA data, connects to an endpoint or invokes Siemens.
A valid public certificate is not an authenticated, trusted, suitable or deployable
certificate. Read-only native store inventory is `certificate_inspect` above; native
assignment, lifecycle and secure key/trust recovery remain WA-08/09
in the [full D12/X03 plan](../plans/expansion-plans/web-server-access-configuration.md).

Implementation uses the platform [single-certificate loader](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509certificateloader.loadcertificate?view=net-10.0)
with explicit envelope/extra-payload checks and bounded [ASN.1 parsing](https://learn.microsoft.com/en-us/dotnet/api/system.formats.asn1.asnreader?view=net-10.0).
Local adversarial checks establish why loader success alone is insufficient.
