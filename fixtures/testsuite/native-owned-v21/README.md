# Genuine owned V21 Test Suite definitions

These are unmodified native `SaveToFile` exports obtained after strict `LoadFromFile` calls in owned offline scratch projects on 15 October 2026. [provenance.json](provenance.json) binds the original import sources, raw native receipts, source snapshots and exact SHA-256 values with relative artifact paths.

| Definition | Native evidence |
| --- | --- |
| [style.xml](definitions/style.xml) | One casing rule authored from a published Siemens schema. Explicit scope request for the owned Program blocks group returned true; one style executor call returned three expected casing violations. |
| [application.tat](definitions/application.tat) | Authored definition references `ScratchPLC` and `TiaMcp_EnableGate_DB.Active`; strict import/export only. |
| [system.tst](definitions/system.tst) | Unchanged vendor documentation example imported/exported; sample endpoint remains text only. |

Effective style scope is **unverified** because no public scope getter was exposed. These fixtures provide no qualification, test-pass, future-execution or runtime authorization. Application/system execution availability remains unknown. Application test-set definitions, complete preserving editors and fresh-reopen/recovery qualification remain outstanding.

The system round trip **cleared `FOLDER_PATH` from `D:\Simatic` to empty**. This is a retained preservation gap, not a preserving round-trip claim. The native application export added default execution-mode/instance properties. The style target block's export changed only its `DocumentInfo/Created` timestamp; both raw exports are retained, without asserting preservation of other peers.

`ts01` retains the failed individual-FC scope request and the unchanged vendor application example's absent-`PLC1` refusal. The latter left the project modified with no application definition visible. `ts02` retains the separately admitted documented Program blocks scope and all three successful definition exports. No automatic replay occurred. The cleanup receipt records exact owned process exits.

Raw receipts preserve original machine paths and bytes. Relative provenance resolves packaged artifacts; checkpoint archives and executable dependencies remain in the original evidence roots and their source hashes were independently checked before packaging. No native calls are made by fixture packaging or offline tests.
