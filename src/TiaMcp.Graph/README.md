# Offline GRAPH documents

`TiaMcp.Graph` is the first isolated document package for roadmap AC-20. It reads GRAPH sequence structure, diagnoses numbered identities and references, compares versions, and produces JSON and a standalone HTML view. It uses .NET 10 and only the base class library. No Siemens runtime, live session, Core, Host, catalog, solution, explorer or native adapter dependency is required.

## Run

From the repository root in PowerShell:

```powershell
dotnet build src/TiaMcp.Graph.Cli/TiaMcp.Graph.Cli.csproj
dotnet run --project tests/TiaMcp.Graph.Tests/TiaMcp.Graph.Tests.csproj

# Choose new output paths; the CLI deliberately refuses to replace existing files.
dotnet run --project src/TiaMcp.Graph.Cli -- inspect tests/TiaMcp.Graph.Tests/fixtures/sequence-v1.xml src/TiaMcp.Graph.Cli/bin/inspection.json
dotnet run --project src/TiaMcp.Graph.Cli -- view tests/TiaMcp.Graph.Tests/fixtures/sequence-v1.xml src/TiaMcp.Graph.Cli/bin/sequence.html
dotnet run --project src/TiaMcp.Graph.Cli -- compare tests/TiaMcp.Graph.Tests/fixtures/sequence-v1.xml tests/TiaMcp.Graph.Tests/fixtures/sequence-v2.xml src/TiaMcp.Graph.Cli/bin/changes.json
dotnet run --project src/TiaMcp.Graph.Cli -- preserve tests/TiaMcp.Graph.Tests/fixtures/sequence-v1.xml src/TiaMcp.Graph.Cli/bin/original.xml
```

The output parent directory must exist. Exit codes are `0` for a written report, `1` for input/output failure, `2` for invalid usage and `3` for a written report with structural errors. Unknown or opaque content produces explicit warnings and does not by itself make the command fail. `compare` uses the same exit codes; finding changes is not an error. `preserve` copies the original bytes, including when structural diagnostics exist. It does not create an import candidate or edit the document.

All project build output stays in the new projects' `bin`/`obj` directories. Tests keep generated reports under their own `bin/.../generated` directory. These projects are intentionally not added to the shared solution or test scripts.

## Library API

```csharp
using TiaMcp.Graph;

var before = GraphDocument.Load("before.xml"); // Also Parse(byte[]) and Read(Stream).
var sequence = before.Inspection.Graphs[0].Sequences[0];
var initialSteps = sequence.Steps.Where(step => step.Init == true);
string json = before.ToJson();
string html = before.ToHtml();
GraphComparison comparison = before.CompareTo(GraphDocument.Load("after.xml"));
string changes = comparison.ToJson();
byte[] retained = before.GetOriginalBytes(); // Defensive copy; WriteOriginal(Stream) also available.
// Typed edits splice the source text; refused results throw GraphException. GraphEditor.Parse(json) reads the same operations.
byte[] edited = GraphEditor.Apply(before, [new GraphEdit("renameStep", Number: 1, Name: "Wait")]);
```

`GraphEditor` supports the operations and refusals described in the [typed edits](../../docs/reference/graph-host-workflow.md#typed-edits) reference. It only produces bytes; it imports nothing.

`GraphInspection` contains ordered graphs/sequences, steps, transitions, branches, connections, actions, interlocks, supervisions, pre/post operations, alarm XML, diagnostics and unsupported fragments. Every node and fragment has a location containing expanded XML names and sibling ordinals. Locations are diagnostic strings, not XPath expressions. XML snippets are inspection serializations, not byte-exact extracts.

`Step` exposes `Number`, `Name`, `Init`, `IsMissing`, `MaximumStepTime`, `WarningTime` and action/condition collections. `Action` exposes its ordinal, event, qualifier and interlock flag with retained XML. `Transition` exposes name, number, missing status, programming language and its `FlgNet`. Branches expose number, type and cardinality. Connections retain document order, typed endpoints, branch `In`/`Out` values and `Direct`/`Jump` link type. Sequence titles display language-tagged text; full multilingual XML is retained.

## Supported structural profile

The profile is `graph-v6-structure.v2`, derived from the installed V21 `SW.PlcBlocks.Graph_v6.xsd` and native captures. Version 2 corrects chart-wide step/transition identity scope and accepts the observed empty-condition export shape with a warning. See [fixture provenance and schema hashes](../../tests/TiaMcp.Graph.Tests/fixtures/README.md).

- Unqualified `Graph` fragments follow the installed chameleon XSD grammar. The v6 namespace binding `http://www.siemens.com/automation/Openness/SW/NetworkSource/Graph/v6` is also read inside a SimaticML envelope, with captured native V21 evidence.
- Graph discovery considers topmost elements named `Graph`; a nested GRAPH-looking element inside a chart's opaque extension is not another chart. Unsupported namespaces are retained and diagnosed, with no guessed compatibility.
- Required structural children, their order and multiplicity, required structural attributes, XML integers/booleans, language/event/qualifier/branch/link values and endpoint shape are checked. Missing or malformed input is not silently repaired.
- Observed native exception: V21 Update 2 exports empty `Supervision`/`Interlock` modules without their schema-required `FlgNet`. The reader accepts that shape with an `EmptyNativeCondition` warning. The captured native no-op import rejects it. No body is inserted, and preservation keeps the exact original XML.
- Step and transition identities are scoped to **chart / node kind / numeric Number**; their numbers must remain unique across sequences. Branch identities are scoped to their sequence. Step 1, Transition 1 and Branch 1 are separate identities. Numeric lexical forms such as `1` and `+001` collide. Jump destinations can resolve to a step in another sequence of the same chart; direct references remain local. Absent, duplicate, cross-sequence and `IsMissing` targets receive distinct errors. Invalid identities remain null and retain their XML. Native exports captured here contain one chart per FB.
- Conditions, action operands/tokens, multilingual modules, alarms and the export envelope are explicit opaque boundaries. Their whole subtree is retained and reported. Unknown direct children and attributes in the supported structural grammar receive specific diagnostics. Unknown descendants inside an opaque boundary are covered by that boundary, not individually classified.

This is **not full XSD validation** of arbitrary input. In particular, embedded `FlgNet` UIds/wires/instructions, symbolic references, branch port ranges and indexing conventions, branch balancing, execution of cross-sequence jumps, alarm IDs, language execution and native ownership identities are not validated. Do not infer execution correctness from the absence of structural errors. Reports always set `fullSchemaValidated` and `nativeQualified` to false. Schema validation and native qualification of recorded fixtures do not certify arbitrary inputs or change those flags.

## Preservation and comparison contract

The reader retains the complete input byte array independently of its projection. Inspection JSON (`tia-graph-inspection.v1`) includes `originalSourceBase64`, input byte count and SHA-256. HTML offers a byte-identical original-source download. Encoding/BOM, declarations, comments, processing instructions, whitespace, ordering and all unknown content therefore survive extraction without reserialization. The model has no native import API; `GraphEditor` changes only the edited spans of a copy.

Comparison JSON (`tia-graph-comparison.v1`) includes both source hashes, diagnostics from both inputs, `sourceBytesChanged`, `matchingReliable` and individual before/after changes:

| Category | Compared content |
| --- | --- |
| `structure` | Node additions/removals/order, initial/missing flags, numbers, branch type/cardinality, connections/ports/order/jumps and graph/sequence count |
| `conditions` | Transition `FlgNet`, programming language, interlocks, supervision and step time limits |
| `actions` | Ordered action XML, including qualifiers, events, operands and interlock flags |
| `metadata` | Step/transition names |
| `retainedContent` | Other attributes/elements/comments, titles, translated names, pre/post operations, alarms, unknown content and the export envelope |
| `unresolved` | Changed node collections whose missing or duplicate identities prevent unambiguous matching |

Graphs and sequences are aligned by **document ordinal**, because this schema gives sequences no stable identity. Nodes match by kind and numeric number. No sequence move/rename inference is made. Count changes, structural errors and ambiguous node identities set `matchingReliable` to false. Even when true, that flag only describes this declared alignment strategy; it is not native identity proof. A source number change is an addition/removal rather than an inferred rename.

XML comparison ignores namespace-prefix spelling, namespace-declaration placement, attribute order and indentation-only text between element children. It preserves child order, comments, processing instructions, mixed text, literal text whitespace and effective `xml:space="preserve"`. Original source byte changes are always reported separately, including encoding, declaration, formatting or lexical changes. These normalizations do not claim behavioral equivalence; `behavioralEquivalenceCertified` is always false. Conditions/actions are compared as retained XML, never evaluated or translated through LAD/FBD semantics.

## HTML and limits

The HTML is self-contained: inline CSS/SVG, internal navigation, expandable XML, diagnostics, ordered connections with branch ports, and retained source. Cross-sequence jumps show a destination node linked to the target sequence and step. No JavaScript, network assets or external renderer is required. All input strings are HTML-escaped and a restrictive content security policy blocks active content. Initial steps have a double border; jumps are dashed. Diagram layout is a document visualization, not native GRAPH editor notation or layout.

At more than 500 diagram nodes, or when numbered identities are missing/duplicated, the diagram is explicitly omitted while the complete node details and connection table remain. Unresolved edges are explicitly counted rather than silently drawn to a different target. Distinct `EndConnection` sentinels remain separate diagram nodes.

Input limits: 8 MiB, 100,000 XML elements, depth 128, diagnostic paths of at most 16,384 characters each and 16 Mi characters in total. JSON/HTML output limit: 96 MiB in UTF-8. Oversize inputs/reports fail explicitly. DTDs and all external XML resolution are disabled. No input-specified schema locations are loaded.

## Acceptance status

On 2026-10-16, all **51 checks passed**: 49 ordinary checks plus two optional checks against the installed Siemens XSD dependency set. The ordinary checks include captured native exports, independently expected sequence structure, receipt/artifact integrity, and preservation comparisons. Browser review verified diagram navigation and expandable native action XML with no console errors or warnings. See [native qualification evidence](../../tests/TiaMcp.Graph.Tests/fixtures/native-v21/README.md).

Native qualification used dedicated, runner-owned TIA V21 Update 2 processes and new scratch projects under the new test directory. Four profiles passed import, software compilation, unchanged-export re-import, save/fresh-process reopen and archive recovery: populated conditions; cross-sequence jumps; simultaneous branches with actions; and alternative branches with actions. Complete exported XML stayed equal after excluding only the export timestamp. The source bytes, timestamp changes and compiler warnings remain recorded.

A separate negative fixture records a native limitation: TIA exports empty interlock/supervision modules without `FlgNet`, then rejects its own unchanged export on import. The reader reports `EmptyNativeCondition` and preserves those bytes. This case is **not roundtrip-qualified**. Another negative fixture records native rejection of duplicate transition numbers across sequences.

Embedded logic remains retained XML. Qualification is limited to the captured TIA/CPU/GRAPH profiles; it does not certify arbitrary documents, unsupported-extension imports, localized step-name import, language-specific editing, runtime/timing behavior, CFC or DCC. Individual inspection/comparison reports therefore keep `nativeQualified` false.

The optional [native runner](../../tests/TiaMcp.Graph.Tests/Native/README.md) is isolated from the Host and shared native session. It has no attach-to-existing-process path and performs no online CPU operation. Library, CLI, fixtures, qualification tooling and build output remain inside the three new GRAPH project directories.
