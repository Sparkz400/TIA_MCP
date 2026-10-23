# Typed capture continuation — 13 October 2026

The user resumed the parallel workstreams. All 38 authored reference inputs now
have independently checked import/compile/save and persistence evidence across
separate attempts. The original c01 remains stopped, and c03 retains its failed
host-close outcome. Separate copied-project readbacks supply their persistence
evidence. No profile was activated and no builder or runtime gate was inferred.

## Accepted c02 reference capture

The independent file-only audit (`artifacts/p17t/c02-independent-audit-01.json`)
passed for the original c02 report: two prerequisite inputs and eight TON route
inputs, 17 added blocks, 36 original objects and 53 final objects. Source, archive,
retained executing harness and binary hashes matched. Exact additions, all peer
PLC XML, the saved/fresh-process reopen snapshots, and both owned lifetimes
(PIDs 61032 and 34820) passed. The original native capture was not repeated.

The eight TON observations cover software-unit and alternate-namespace routes,
LAD and FBD, and global and multi-instance storage. They establish native
reference import/compile/export and persistence. They do not establish typed
builder acceptance, recovery after a changed-state failure, or timer runtime.

## c01 prefix persistence

The prefix audit (`artifacts/p17t/c01-prefix-independent-audit-01.json`)
passed all eleven completed inputs, 18 exact additions, the 54 final PLC objects,
all prior peer XML and the original exact owned cleanup. Its nineteen originally
undispatched sources were admitted separately in c03 below. The original `capture.json` still reports
`stopped_by_request`; individual successful save receipts were not relabeled as
fresh-process persistence evidence.

The new f01 finalization (`artifacts/p17t/f01/finalization.json`) hashes every
file in the closed historical owned project, copies that directory into a new
owned output, verifies exact copy equality, then opens only that copy in one
fresh owned TIA process. No compile, save, source import, checkpoint restore,
simulator, or physical PLC operation is admitted. All 54 native payloads matched
the last accepted c01 snapshot. The comparator retains export provenance,
namespaces, whitespace, comments, processing instructions, CDATA, native IDs and
ordering; only a simple direct `DocumentInfo/Created` timestamp text is normalized.
Unknown provenance and non-simple timestamp content remain exact.

The independent finalization audit (`artifacts/p17t/c01-finalization-independent-audit-01.json`)
also passed all 54 payload and provenance comparisons, unchanged hashes for all
nine original project/companion files, the exact copy-only native open, the
original fa0067 binary bundle, and exact cleanup of owned PID 35492. The launcher
exited zero and all fifteen retained transitive harness sources were unchanged.
This supplemental evidence closes saved-prefix readback for the eleven completed
inputs. It does not turn the stopped 30-input attempt into a completed capture.

## c03 saved references and separate persistence

The fresh all19 admission (`artifacts/p17t/c03-all19-admission.json`) used the
original fa0067 bundle, pinned sources and fifteen retained helper files. All
nineteen standalone local `Int` comparisons imported, compiled and saved in one
new owned archive retrieval. The saved-reference audit (`artifacts/p17t/c03-saved-independent-audit-01.json`)
passed 1,784 native XML exports, 1,765 strict peer/provenance comparisons, nineteen
exact additions and the final 55-object saved snapshot.

The original run then failed host cleanup before admitting its planned reopen.
TIA PID 28404 exited through its original captured handle; the host exited 1
without force, and worker 16628 had not exited within the 30-second receipt bound.
Later read-only process inventories found the worker absent and no selected
bundle processes remaining. The cleanup supplement (`artifacts/p17t/c03-all19-cleanup-supplement-01.json`)
retains these different facts. It does not invent a clean worker exit code.
The full-capture acceptance audit (`artifacts/p17t/c03-all19-independent-audit-01.json`)
correctly remains failed, and the original capture remains `blocked`.

The separately reviewed f02 admission (`artifacts/p17t/f02-admission.json`)
bound that exact failure, saved-only audit, final 55-object snapshot and later
process observation. Its explicit mode preserves the default stopped-prefix
guard, which still rejects c03. It copied all nine original project/companion
files, verified byte equality and opened only the new copy. No original reopen,
compile, save, retrieve, mutation, restore, simulator or physical PLC operation
was admitted. The independent f02 audit (`artifacts/p17t/c03-finalization-independent-audit-01.json`)
passed all 55 payload/provenance comparisons, unchanged original files and exact
clean exits of new TIA 43516, host 42428 and worker 55648. All fifteen helpers
remained unchanged. This accepts fresh copied-project persistence while retaining
the original failed cleanup.

The packet reconciliation (`artifacts/p17t/reference-packet-independent-audit-01.json`)
maps all 38 distinct authored inputs to 45 reference blocks: eleven c01 prefix
inputs, eight distinct c02 TON inputs and nineteen c03 inputs. The two c02
prerequisite repetitions count once. These are separate evidence chains, not a
relabeled successful combined capture or changed-state recovery result.

## Inactive profile candidates

The extractor now supports `--stage-output` in a new directory under repository
artifacts. It retains deployable fixtures unchanged, rejects append mode and
existing output reuse, binds exact native read/export identities, rejects missing
scope/namespace metadata and duplicate native filenames, and confines every
candidate filename to its staging directory. These controls have ten focused
offline checks. The intermediate stage02 attempt rejected an inventory/read
metadata-shape difference before activation and remains retained; stage03 matched
the exact stable read fields and exported bytes successfully.

The four staged TON candidates (`artifacts/p17t/c02-profile-candidates-03/profiles.candidate.v1.json`)
and their independent audit (`artifacts/p17t/c02-candidate-independent-audit-01.json`)
map all eight route/storage/language cells to exact authored, native-export,
staged-export and profile hashes. Native Part, network and multi-instance member
fragments match their recorded sources; all builder engineering, preservation,
persistence, recovery and runtime flags remain false. No profile was activated.

The comparison candidates (`artifacts/p17t/comparison-profile-candidates-01/profiles.candidate.v1.json`)
and their independent audit (`artifacts/p17t/comparison-candidate-independent-audit-01.json`)
bind 37 unique captured export filenames to source hashes, scoped identities and
exact native/profile hashes. Twenty-two new inactive comparison profiles, plus
the two retained software-unit Eq references, cover all 24 route/language/operator
reference cells. Native pins, local operands, result-coil wiring and complete
network/Part fragments were checked. Two auxiliary namespace numeric-call rows
remain inactive and are outside this comparison qualification.

Four global/multi candidate-template entries remain unresolved because their two
template callees lack authored-source/candidate-catalog lineage in this packet.
The prerequisite audit (`artifacts/p17t/candidate-prerequisite-audit-02.json`)
confirms that both namespace template FBs and the software-unit global timer DB
already exist in the native baseline and persist. Their native existence does
not supply the missing candidate source lineage. These gaps must be resolved
before support activation.
The observed global instance component names are fully namespace-qualified.

## Harness checks and remaining gates

The capture compile gate now accepts the documented successful `Information`
target state while retaining complete diagnostics, explicit integer zero error
counts and recursive rejection of Error/unknown states. Owned cleanup now retains
host PID, every observed stderr line, EOF completeness and close/constructor
exceptions with tracebacks. EOF is observed before copying stderr so a final
reader line cannot be omitted while claiming completeness. Shutdown bounds and
strict failure semantics are unchanged. Independent reviews cover
cleanup diagnostics (`artifacts/p17h/owned-cleanup-review01.json`)
and saved-copy admission (`artifacts/p17h/saved-copy-finalizer-review01.json`).

Focused results: 41 typed-batch checks, 23 finalizer checks, 24 existing owned
lifetime checks and ten candidate-staging checks. The earlier
combined validation (`artifacts/p17r/combined-validation-01.json`) passed
offline/browser checks on its separately recorded build; later diagnostics and
guard edits enter the coordinator's next frozen validation. Native c01/c02/c03
and f01/f02 retain their exact fa0067 build lineage.

Still open: profile activation review and missing candidate source lineage;
missing scoped storage/dependency captures; builder engineering/recovery
qualification for newly enabled routes; numeric/FB/timer/counter runtime; native
insertion with all unrelated IDs preserved; and final combined-build acceptance.
The c03 original host/worker shutdown acceptance remains failed; copied readback
does not repair that historical gate. Twenty-two comparison, four TON and two
auxiliary call candidates remain under artifacts only.
The existing production insertion rejection and all runtime isolation boundaries
remain unchanged.
