# HMI relationship evidence in V21

The adapter distinguishes explicit configured foreign keys from coincidental equal names. Its relationship records remain read-only evidence; they do not authorize a change.

Target profile discovery first uses native catalog data on the exact runtime's ancestors. When the catalog-bearing panel is a sibling of its runtime, it enumerates the actual owning device's items under the shared traversal budget and a 128-item local bound. A sibling catalog is accepted only with one family-compatible native catalog candidate and one exposed runtime of that family in the completely observed device scope. Multiple runtimes, multiple candidates, missing metadata or incomplete traversal leave the profile unknown. `catalogEvidence` retains native source paths and the association rule; device names and article-number guesses are not classifiers.

## Native endpoint proof

The installed `PublicAPI/V21/net48/Siemens.Engineering.Base.xml` documents these public members:

- `Siemens.Engineering.HW.Features.CommunicationManagement.Connections`
- `Siemens.Engineering.HW.CommunicationConnections.Connection.LocalTarget` and `PartnerTarget`, both native `DeviceItem` references
- `Connection.IsValid`, `LocalInterface` and `PartnerInterface`
- `Siemens.Engineering.HW.CommunicationConnections.HmiConnection.LocalConnectionName`

V21 introduced public communication-connection management, including HMI connections, according to [Siemens' V21 release description](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/what-s-new-in-tia-portal-openness).

The bridge probes `CommunicationManagement` on the exact HMI runtime device item. A native HMI connection must identify that same local runtime by native object equality, be valid, and identify exactly one observed PLC endpoint by `PartnerTarget` or its native `PlcSoftware` object. Only then does its scoped `LocalConnectionName` connect the software connection row to that PLC. A same-name device elsewhere in the project cannot satisfy the endpoint comparison. Missing providers and invalid or remote partners remain unresolved.

## Configured symbolic declarations

Siemens describes `HmiTag.Connection` and `PlcTag` as configured tag properties, while `PlcName` is read-only. See [accessing tag properties](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/tags/accessing-tag-properties). The [connection property reference](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/connections/accessing-connection-properties) defines `Partner`, `Station` and `Node`; these strings alone are not substituted for native endpoint identity.

[Siemens' addressing documentation](https://docs.tia.siemens.cloud/r/en-us/v21/configuring-tags-rt-unified/basics-rt-unified/addressing-external-tags-rt-unified) distinguishes integrated and non-integrated connections and restricts symbolic addressing to integrated connections. The bridge requires an exact native connection endpoint proof, `SymbolicAccess`, a compatible primitive declaration type, and a unique complete declaration lookup in the selected standard PLC scope. A nonempty `PlcName` must also agree with the proven native PLC.

For Unified, plain configured PLC symbols resolve to native `PlcTag` identities. Explicit scalar DB paths resolve against a retained native block export through the existing `OfflineEvidence.NormalizeInterface` reader; the returned declaration locator includes its exact block identity, section, path components, export SHA-256 and XML line. Quotes preserve identifier boundaries. Dynamic expressions, local symbols, array indices, multiplexing, unit/namespace ambiguity, unresolved UDT expansion and incomplete traversal remain unresolved. No matching address or display name is sufficient by itself. Classic does not inherit Unified `PlcTag`/`AccessMode` semantics; it requires its separately evidenced XML binding shape.

## Screen bindings

For Unified, [the native tag-dynamization properties](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/screens/dynamization/accessing-tag-dynamization-properties) explicitly identify the configured HMI tag and whether indirection is enabled. A direct foreign key resolves only within the exact HMI software and a completely inspected candidate scope; a sole candidate in an incomplete scope stays unresolved. Indirect or expression dynamics retain their native metadata and unresolved status. Project scripts are never executed.

The adapter also reads the tag dynamization's value converter. [Siemens documents formula expressions](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/screens/dynamization/accessing-tag-dynamization-formula) that can contain multiple tags and operators. `IsFormulaSelected=true`, missing converter metadata, and indirect addressing prevent promotion of the nominal `Tag` field to a direct binding; the formula text stays in the captured metadata.

For each Unified screen/item, the reader visits its public `Dynamizations`, `EventHandlers`, and `PropertyEventHandlers` compositions when the exact native type exposes them. It captures scalar event/trigger metadata and `IHmiScript` source text, SHA-256, global-definition text/hash, and async state. Each construct reports observed, unavailable, not exposed, or truncated coverage. An exact-screen relationship request includes the screen's own behaviors as well as its items. `hmiScriptBehavior` edges remain dynamic and unresolved: there is no qualified JavaScript/static-navigation parser, and comments or strings containing a tag/screen name are not promoted to bindings. Text above 8,192 characters is visibly truncated with the full native text hash retained; each behavior composition is bounded to 256 KiB and the shared capture to 8 MiB.

General screen inventory defers behavior/source capture. Select an exact screen `objectPath`, or request its items with `includeScreenOwner:true`, to inspect screen-owned behaviors; ordinary item enumeration does not add the screen owner to its rows. The same owner option on Classic returns the native screen metadata with the retained XML artifact and an explicit unresolved behavior-parser limitation. Scalar getter failures on a broken reference remain diagnostics and unavailable metadata; they do not invalidate a separately completed declaration-candidate enumeration. Collection traversal failures, missing identities and exhausted budgets still prevent uniqueness claims.

The `resources` section covers native text lists and graphic lists. It explicitly excludes standalone images, fonts, global resource repositories, and unknown resource types; complete coverage of that supported subset does not claim complete coverage of every HMI resource.

Classic screen payload inspection follows retained native exports. The native V21 fixture demonstrated `ObjectName` on a rectangle and `Name` on its containing `ScreenLayer`; both are retained, with a separate structural layer kind. Unknown Classic binding XML is retained without treating an arbitrary `<Name>` element as a tag reference. A supported Classic binding parser requires an independently exported native binding shape.

Classic link, event and navigation payload observations retain bounded leaf text, relative XML element paths, native link attributes and the exact export artifact. They remain unresolved, including when a screen or tag has the same name. Faceplate interface values likewise emit explicit `hmiFaceplatePort` observations without guessing port binding semantics. Native library-version references continue to belong to the library subsystem.

## Alarm tag references

When `includeAlarms` is requested, Unified discrete and analog alarms use the documented `RaisedStateTag` foreign key and `AlarmParameterTags` list, limited to ten native string entries. Discrete alarms additionally expose `AcknowledgmentStateTag` and `AcknowledgmentControlTag`, with their associated bit numbers. These properties are documented in Siemens' [discrete alarm reference](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/discretealarms/accessing-discrete-alarm-properties) and [analog alarm reference](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/analogalarms/accessing-analog-alarm-properties), and in the installed V21 WinCC Unified XML documentation under `HmiDiscreteAlarm` and `HmiAlarmCommon.AlarmBase`.

An alarm edge resolves only to a unique completely observed tag in the same exact HMI software. Parameter indices and trigger/acknowledgment bits remain explicit; empty slots do not create bindings. Missing, ambiguous, unreadable and truncated values remain unresolved. Expression-like text is not evaluated, member expansion is not guessed, and the referenced tag's own connection or dynamic configuration remains a separate relationship. The bridge implements these edges with pure positive/negative tests; native alarm-edge qualification remains unverified until a bridge run checks them against the independent rich fixture.

## Limits and qualification

The complete relationship capture is limited to 2,000 edges and 4 MiB, with additional native traversal, recursion, response-page and source-export limits. DB declaration parsing consumes a combined 16 MiB export budget and at most 32 distinct block exports. Exhaustion is explicit and cannot certify missing references or declaration uniqueness.

API presence, bridge implementation and native qualification are separate. See the qualification reports for exact device/runtime fixtures. The endpoint and symbolic-declaration implementations above do not claim successful native qualification until the independent multi-PLC fixture has passed. Runtime communication and physical deployment are outside these inspection operations.
