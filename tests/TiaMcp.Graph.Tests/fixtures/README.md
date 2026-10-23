# GRAPH fixture provenance

Top-level XML files are authored test/import inputs. Files named `baseline.xml`, `roundtrip.xml`, `reopened.xml` and `recovered.xml` under `native-v21/` are unmodified exports captured from TIA Portal V21 Update 2 on 2026-10-16. Native `seed.xml` files remain authored inputs. No Siemens schema files are redistributed here.

| Fixture | Independent expected content |
| --- | --- |
| `sequence-v1.xml` | One sequence; steps 1/2/3 (`Idle`, `Run A`, `Run B`); transitions 1/2 (`Start`, `Reset`); simultaneous branches 1/2 (`SimBegin`, `SimEnd`) with cardinality 2; eight ordered connections ending in a jump back to Step 1. Step 2 action is event S1 / qualifier N / interlock true / MotorA. Step 3 has two ordered actions. Step 2 has T#10s maximum and T#8s warning time, a GuardClosed interlock and a Travel timeout supervision. |
| `sequence-v2.xml` | Exactly four declared changes from v1: Step 2 maximum time T#10s → T#12s; Step 2 action qualifier N → S; Transition 1 symbol StartCommand → StartPermitted; final connection changes from Jump to Step 1 into Direct to EndConnection. |
| `unknown-envelope.xml` | Synthetic SimaticML-shaped wrapper, v6 namespace, one genuine structural Step plus a foreign lookalike, foreign attributes/elements, nested foreign Graph, CDATA, Unicode, processing instruction, comments, hostile HTML-like text and a non-GRAPH sibling compile unit. It tests retention and escaped display, not XSD validity or native acceptance. |

The ordinary tests use no installed TIA files. An optional additional test uses .NET's independent `XmlSchemaSet` validation of the two unqualified version fixtures against an explicitly selected installed schema directory:

```powershell
dotnet run --project tests/TiaMcp.Graph.Tests/TiaMcp.Graph.Tests.csproj -- --schema-dir 'C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\Schemas'
```

The installed schemas inspected and used on 2026-10-16 were:

| File | SHA-256 |
| --- | --- |
| `SW.PlcBlocks.Graph_v6.xsd` | `6632f3676254f20087c3bb63469923f5863453ed08c42cb2fa6ed6702becfa0e` |
| `SW.PlcBlocks.Access_v5.xsd` | `5b29b6cd965b123f35b8e6916f7f412b8bd4b476858e11bfeabace020aea557d` |
| `SW.PlcBlocks.LADFBD_v5.xsd` | `e5a32746784452e400ca2a73fedb7a750950ce51632d8151aba7dc8d039a8c8f` |
| `SW.PlcBlocks.CompileUnitCommon_v5.xsd` | `38c7e4b87efabe370e67665a42ebc1693df2781c241408d7aa825051403714b9` |
| `SW.Common_v3.xsd` | `183d17bcdab57ec465c2ff744fafb94a89caf217ad642562e68b740d18966f53` |

XSD validation establishes syntax for the authored version fixtures. Native evidence is separate: see [native qualification](native-v21/README.md) and the artifact hashes in [manifest.json](native-v21/manifest.json). Neither schema validation nor native compilation proves runtime behavior. Embedded action and condition XML remains retained, not interpreted.
