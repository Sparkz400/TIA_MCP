# Independent vendor intake review - 25 October 2026

**Complete for the retained library/source distributions.** Codex independently
read the downloaded bytes, license documents and prerequisites after Claude's
native vendor qualification. This closes the independent intake-review item;
it does not qualify additional native operations, vendor executables or deployment.
The [review manifest](vendor-intake-review-20261025.json) retains exact hashes,
archive inventories, license locations and the separate remaining gates.

## Inputs and integrity

Reviewed `D:\lcq\vendor\receipts.json` and all seven retained ZIPs without changing
them: 467,587,713 bytes and 236 outer ZIP entries. All seven file sizes/SHA-256s,
all eight recorded license/document hashes, and all 18 recorded inner-archive
size/hash pairs match. ZIP CRC checks passed; outer paths contain no traversal
and names are unique. The review also hashes all 23 native library/project
archives, including the five V19 archives whose hashes were absent from the receipt.

| Retained distribution | Inventory confirmed | License notices in that distribution |
| --- | --- | --- |
| PAL, commit `e46b222ed9fb736456ccda40c4aa33af6c143b32` | 55 SimaticML XML files, all Engineering V16 | MIT; Michael Gledhill, 2020 |
| DMC V19 / Open Library 19.0 | Five `.zal19` libraries | MIT; DMC, 2019; Modbus: Ola Bjørnli, 2021 |
| DMC V18 / Open Library 18.0 | Four `.zal18` libraries; no combined library in this ZIP | MIT; DMC, 2019; Modbus: Ola Bjørnli, 2021 |
| DMC V17 / Open Library 5.0 | Five `.zal17` libraries | MIT; DMC, 2019; Modbus: Ola Bjørnli, 2021 |
| DMC V16 / Open Library 4.1 | Three `.zal16` libraries | MIT; DMC, 2019; Modbus: Ola Bjørnli, 2018 |
| DMC V15 / Open Library 4.0 | Three `.zal15` libraries | MIT; DMC, 2019; Modbus: Ola Bjørnli, 2018 |
| DMC V14 SP1 / Open Library 3.0 | Two `.zal14` libraries and one `.zap14` example project | MIT; DMC, 2018; Modbus: Ola Bjørnli, 2017 |

The [publisher's DMC page](https://openplclibrary.com/) still links all six exact
download URLs in the receipts. PAL's downloaded `LICENCE.md` is byte-identical
to the [license at the pinned upstream commit](https://github.com/practicalseries/PS2001-pal-software/blob/e46b222ed9fb736456ccda40c4aa33af6c143b32/LICENCE.md).
These checks establish receipt consistency and source references, not a publisher
signature or an independent trusted digest of each DMC download. The original
download-time TLS/header statements were not independently replayed.

## License review and boundaries

The bundled MIT notices require retaining their copyright and permission notices
in copies or substantial portions, and include the warranty/liability disclaimer.
Keep the separate Modbus notice with its material. The current DMC website says
2026; that does not replace the 2018/2019 notices shipped in these historical ZIPs.
PAL's README year also differs from its license; retain the actual license text.
No vendor payload or license text is added to a TIA MCP release by this change.

Every DMC ZIP also contains a nested Windows toolbox ZIP. Its listing contains
25 executable/DLL files and no separately named license/notice file. Only its
container hash and listing were inspected; its dependencies and redistribution
terms were not qualified. The toolbox was not extracted or executed. Any future
toolbox bundling needs a separate dependency/notice review. This review does not
grant rights to Siemens products or SDK assemblies.

## Prerequisites and remaining qualification

- **PAL:** the pinned README describes a VCI workspace for an S7-1200/S7-1500
  controller named `CON100`; both `Default tag table.xml` and `PAL_SystemTags.xml`
  are present. The source ZIP is not the separately offered archived TIA project.
  Existing [native evidence](../../evidence/local.library_vendor.20261025.json)
  records V21 refusing this V16 XML. The intermediate-version import/upgrade
  route remains unqualified here.
- **DMC CPU setup:** enable both system and clock memory bytes on each consuming
  CPU, and include the `Open Library` tag-table master copy. The guide permits
  different byte addresses because its accesses are symbolic; screenshot values
  are examples, not a universal address requirement. The tag-table route already
  has [native evidence](../../evidence/local.library_dmc_prerequisites.20261025.json).
  System and clock memory bytes are now set through `change.hardware_cpu_memory_bytes`
  ([evidence](cpu-memory-bytes-20261026.md), 26 October). DMC FB compilation and
  behavior stay open because the DMC archive is no longer on this PC.
- **DMC product selection:** choose the actual archive and installed products.
  The V17/V18/V19 ZIPs share the same 2 September 2022 setup PDF, whose table names
  V17. Its PLC-only row has no HMI content; its Professional and Unified rows also
  include Comfort/Advanced content. This table describes contents, not proof of
  installed licenses, absence-of-product behavior or V21 HMI acceptance. The
  V15/V16 guide is dated June 2019; the V14 guide is dated December 2018. Do not
  apply the later table to earlier archives or infer an absent V18 combined file.
- **HMI and release:** existing upgrade/readback evidence does not qualify
  faceplate behavior, SiVArc, visuals, protected content or whole-vendor runtime.
  Those checks, native scale and final package acceptance remain in their lanes.

The license page in each of the three distinct setup PDFs and the current
memory-byte, product-table and tag-table pages were rendered and inspected.
All original package and receipt hashes were checked again after reading.
Raw inspection, extracted document copies and renderings are retained locally
under `artifacts/vendor-review-20261025/`; the committed manifest is sufficient
to identify the reviewed inputs without redistributing vendor payloads.
