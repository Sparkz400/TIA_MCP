# Independent graphical references

The program files in `sources` are independently authored SIMATIC SD or SCL
programs. The `route-*-native-*.xml` probes are explicitly derived native
templates, with per-file `.provenance.json` records of their input hashes and
adapted fields. Neither category uses output from the typed network builder.
Only successfully captured references enter the catalog. Siemens TIA Portal V21
Update 2 imported, compiled and exported the successful references recorded in
`profiles.v1.json`. The native files retain the exported bytes and SHA-256.
The installed Step7 API file version is `2100.0.121.1`; the captured network and
interface namespaces are FlgNet v5 and Interface v5.

The catalog binds each instruction to the observed language, CPU type identifier
`OrderNumber:6ES7 511-1AL03-0AB0/V4.1`, route and raw reference hash. An absent
combination is unsupported. A native source reference compiling successfully
does not establish that the typed builder, its preservation checks or its
runtime behavior have passed.

The standard route has separate LAD and FBD Boolean, comparison (`Int`), call,
timer (`Time`) and counter (`Int`) reference captures. Timer/counter references
include local and global instance storage. The explicit NOT reference is the
native one-input `A` part with `Negated Name="in1"`; the separate inverted-coil
reference is only a reader profile. The source Boolean FBD reference also
contains native input-pin inversion, represented by a distinct reader profile.

Software-unit and alternate-namespace references are captured separately from
a retrieved SHA-bound scratch archive. Their presently captured instructions
include Boolean operations, contacts, coils and branches, with separate FC/FB
creation envelopes. The default unit also has independently captured `Int`
equality and numeric FC/FB calls, including local and global FB instances.
The default unit namespace is the `softwareUnit` route;
the explicitly selected alternate namespace is the `namespace` route. These
captures do not qualify instructions absent from those route-specific profiles.
The native creation probes retain `AutoNumber=true`, matching Siemens'
[V21 software-unit programming rules](https://docs.tia.siemens.cloud/r/en-us/v21/using-software-units-s7-1500/creating-a-plc-program-in-software-units-s7-1500).
Those rules also permit IEC timers and counters; a failed SIMATIC SD import
does not establish that the underlying software-unit target prohibits them.

Creation references contain 17 scalar types in each eligible declaration
section. FC return type is `Void`. Global FB instance templates and local
instance declarations retain native declaration metadata, rather than guessing
hidden storage layout. Unknown types, sections and return shapes require a
captured profile before creation can be supported.

`authoredSourceSha256` hashes the exact checked-in source bytes.
`transportSourceSha256` hashes the source text sent through MCP after Python's
documented newline normalization. The capture records retain source paths,
change IDs, compiler results and exported native artifacts. `builderUsed` is
false for these source captures. Each of the five typed qualification gates
remains independent: engineering, XML preservation, persistence, recovery and
runtime.

`behavior.py` defines independently authored expected results. The qualifier
uses the existing block-test program generator and owned PLCSIM backend.
`scripts/typed_graphical_native_expectations.py` additionally compares native
topology with the reference, including input inversion and pin identity. This
semantic comparison does not replace complete native XML readback comparison.
The latter retains IDs, ordering, attributes, comments, interfaces and unrelated
networks under the repository's existing native-document comparison rules.

The CTD and CTUD vectors also cover signed `Int` saturation after loading the
boundary value. These expectations follow Siemens' [CTD V21 semantics](https://docs.tia.siemens.cloud/r/en-us/v21/lad-s7-1200-s7-1500-s7-1200-g2/counter-operations-s7-1200-s7-1500-s7-1200-g2/ctd-count-down-s7-1200-s7-1500-s7-1200-g2)
and [CTUD V21 semantics](https://docs.tia.siemens.cloud/r/en-us/v21/graph-s7-1500/graph-actions-s7-1500/counter-operations-s7-1500/ctud-count-up-and-down-s7-1500).
CTU has no load pin in the captured interface. Its separate `ctu-upper-limit`
driver vectors issue bounded batches of rising edges to reach signed `Int`
maximum, then assert saturation and reset. The short direct CTU vectors cover
edges, held levels and reset. All vectors require actual execution before
their behavior is qualified.

Run `scripts/capture_typed_graphical_profiles.py` or
`scripts/capture_typed_graphical_routes.py` for new source-reference evidence.
Both use `change_prepare`, `change_apply`, `change_status` and `change_restore`
through actual MCP requests. For an inconsistent block that TIA refuses to
export, the source import may have no after-fingerprint and `change_restore`
can reject with `StaleRevision`. The route capture tool's explicit
`--retrieve-after-restore-rejection` option preserves that diagnostic and the
original project, then retrieves the SHA-bound checkpoint into a new owned
instance and compares all known native payloads. This is recorded as
`checkpointRetrieveRecovery`; it is not a successful `change_restore` or typed
recovery qualification. Run `scripts/qualify_typed_graphical.py` for typed
builder evidence. Runtime assertions run only with its explicit `--live` mode;
an engineering-only pass does not mark runtime verified. Failed typed applies
retain their checkpoint recovery evidence and any separately observed compiler
success.

`author_wave9_families.py` writes the not yet captured standard-route references,
capture packet and cases for DInt/Real comparisons, MOVE, arithmetic, TONR and
R_TRIG/F_TRIG (`wave9-capture-packet.v1.json`, `wave9-cases.v1.json`). Their SIMATIC SD
for MOVE, arithmetic and edge boxes is unverified. See
[the wave 9 runbook](../../docs/verification/typed-wave9-families-runbook.md).

`crossscope-cases.v1.json` (written by `author_advanced_route_cases.py`) calls
TiaMcp_UnitA's numeric FC from its alternate namespace and from the related unit
TiaMcp_UnitB against the references captured from
`route-capture-crossscope-call-packet.v1.json`; not yet run.

`route-advanced-cases.v1.json` contains requests with available independent
route references. `route-pending-references.v1.json` records requested cases
whose reference capture remains unavailable. Their absence from the executable
case set is not a pass. SIMATIC SD unit IEC probes currently retain the native
`Missing instance DB` compiler diagnostic; their raw-XML alternatives require
independent native acceptance before adding profiles.
