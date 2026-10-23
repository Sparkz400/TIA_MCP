# Isolated native qualification after a full OS boot

The default per-user native registry still refuses the unresolved 20 October
`hmic20c` attempt. Its diagnostics-reporter lifetime remains unknown and its
original coordinator cleanup remains failed. No ticket is deleted or completed
by the separate isolation route.

`prepare_hmi_fixture_ui.py --isolate-prior-boot` explicitly selects a narrow
exception for a **new local disposable fixture**. It accepts only the reviewed
original intent and reconciliation SHA-256 fingerprints. Every original source
reference and the configured Classic archive must remain available and unchanged.
The old mutable project must be below the original artifact root; the new output
must be disjoint, absent, free of reparse ancestors and on a local fixed volume.
UNC paths, mapped network drives, another unresolved ticket, incomplete evidence
and any changed original bytes refuse.

Admission observes the current local Windows boot using CIM `LastBootUpTime`,
the latest Kernel-General event 12, `GetTickCount64`, and a matching Kernel-Boot
event 27 with `BootType=0`. Machine names must agree across current observations.
Clock disagreement, missing/ambiguous event fields, a hybrid boot or resume,
postboot historical receipt metadata and a changed boot within the held window
refuse. The old retained birth times and receipt creation/modification times must
precede that corroborated boot by more than sixty seconds.

The historical records lack an authenticated machine identifier. The exception
uses the exact existing local registry and artifact provenance as an explicit
assumption, rather than claiming historical machine authentication. All new
mutation resources are local and disjoint: a historical process on another host
could not own them, and the corroborated full boot separates preboot lifetimes on
the current host. This is engineering admission for a trusted local coordinator,
not a security boundary against an adversarial same-user process or forged OS logs.

Windows fast startup can retain kernel state; shutdown alone is therefore not
sufficient evidence. The independent kernel-uptime agreement and matching full
boot event are mandatory. See Microsoft's [startup/shutdown description](https://learn.microsoft.com/en-us/windows-hardware/test/weg/delivering-a-great-startup-and-shutdown-experience)
and [system power states](https://learn.microsoft.com/en-us/windows/win32/power/system-power-states).

The original registry lock remains held before store locks. A second coordinator
cannot enter it. The current boot, exact old sources and fresh target are checked
again immediately before creating a new dispatch intent and starting the helper.
`OwnedProcessJob` assigns the helper to its original non-breakaway job atomically
in `CreateProcessW`; failure has no uncontained fallback. The native helper claims
the target with `CreateDirectoryW`, refusing a directory that appeared after
admission. The exact main-editor birth/image/ancestry and retained handles are
still required. New completion requires every retained handle, the original
helper and zero active processes in the original job. Brokered/alternate-parent
processes remain outside the ordinary descendant guarantee.

An isolation observation records `historicalCleanupVerified=false`,
`originalAttemptPromoted=false` and `legacyTicketModified=false`. It grants no
reusable admission, automatic replay, physical PLC operation, runtime execution
or production HMI eligibility. Any new unresolved attempt blocks subsequent
dispatch even if its files later become old.

`qualify_native_reboot_isolation.py` exercises the actual local admission and a
competing-owner refusal without starting TIA or creating a project. Each actual
native attempt must repeat admission under its own held window. See the
[qualification record](../verification/native-isolated-dispatch-20261021.md).
