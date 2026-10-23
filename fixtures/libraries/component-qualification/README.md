# Component qualification fixture

This independently specified engineering fixture uses an owned authoring scratch,
a committed FB with an exact UDT dependency, and a separate owned consumer. Native
type/version GUIDs and dependency edges are observed; none are fabricated.

The consumer's standalone FBD FC is absent before import. Installation must add
exactly the FB, UDT, global instance DB and one-network FC, preserving every
original PLC block/type/tag-table XML payload, including all OBs and an unrelated
FC. Calling the new FC from an OB remains a separate change. Native runtime and
production combined apply remain unqualified.

The earlier existing-caller attempt stopped before import at the strict native
metadata preservation guard. The first standalone installation attempt imported,
compiled and saved the exact FB/UDT, then correctly rejected unequal FB version
exports. The installed version explicitly contained `AutoNumber=true`,
`IsIECCheckEnabled=false` and `MemoryLayout=Optimized`, while its source version
omitted those three content attributes. They are not ignored as export metadata.

The current FB authoring input is `native/TiaMcpComponentGate.xml`, which explicitly
specifies those settings. `native/provenance.json` identifies and hashes the
retained source/installed exports and exact adaptation: extract the complete FB
from its library-version wrapper and remove only `CompositionName=ContentObject`.
All FB attributes, interface, implementation tokens, comments and native IDs are
retained. The SCL file is an independently authored semantic reference, rather
than the root input passed to `CreateFromDocuments`. Before consumer component
dispatch, the pinned source version export must explicitly contain those exact
settings. A serializer that omits them remains blocked. Generic source/installed
raw SHA-256 equality is unchanged; no omitted-default inference is enabled.

A passing engineering run requires each reviewed import/typed preview, exact
version/content/dependency/use readbacks, independent call/instance/tag wiring,
complete PLC preservation, fresh reopen, retrieval of the first import checkpoint
into a new owned location, and recovered reopen. Failed or uncertain installation
never triggers automatic replay, continuation or recovery. Captured but rejected
readbacks remain explicitly unverified in the component journal.

`--recover-from-report <failed-result.json>` performs a separately requested
first-checkpoint recovery through `project_retrieve`, with exact checkpoint/hash
lineage, seven-object (fixture-dependent) baseline comparison, fresh reopen and
component-library absence checks. It neither dispatches installation operations
nor edits the original report, baseline exports, archive or first-import journal.
An uncertain retrieval is never retried by the runner. Native qualification of
the explicit XML input and the separate recovery route remains required.
