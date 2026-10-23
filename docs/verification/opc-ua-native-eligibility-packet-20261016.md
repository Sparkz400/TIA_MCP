# OPC UA UA-03 / UA-Q00 next eligibility and export packet

Prepared 16 October 2026. This packet defines the next bounded native increment after the [offline Host workflow](../reference/opc-ua-workflow.md). **No native run is admitted or executed by this delivery.** The selected project/CPU, current process/resource admission and frozen native implementation still require binding. Historical TIA PIDs, simulator receipts and parser success cannot fill those fields.

The first native packet is read-only eligibility plus one explicitly selected native export. It must not combine mapping writes, namespace import, reference generation, server activation, license changes, access/security changes, compile, endpoint communication or runtime testing. The wider UA-Q00 import/generation experiments in the expansion plan remain separate subsequent packets.

## Exact admission manifest required before execution

| Field | Required evidence | Current disposition |
| --- | --- | --- |
| Source and binaries | Frozen source manifest; Host/worker/module and Siemens API DLL SHA-256; exact TIA V21 product/update | Offline Host hashes are retained; native UA adapter is not implemented/admitted |
| Resource ownership | Current serialized TIA queue admission, process lifetime controller, fresh ownership/cleanup receipt | Not acquired by this lane; translation and watch own the current native queue |
| Project/CPU | Exact project path/hash, PLC scope and full native owner ancestry, CPU order number/type identifier and firmware, instance and engineering cultures | No selected target supplied; do not infer one from previous fixtures |
| Product and rights | Fresh service observations and documented owner; required engineering/license observations with unknown distinct from absent | Not observed on a native target |
| Export selection | One exact interface category/name/native identity, selected export method/format, reviewed destination under the artifact store | Must be bound after read-only eligibility; no broad PLC-address-space fallback |
| Before inventory | Full selected interface bytes/metadata plus sibling interface inventory and independent relevant source/peer fingerprints | Required before native export |
| After verification | Exact export bytes/hash, complete diagnostics, unchanged project/source/peer evidence and fresh independent readback | Pending native packet |
| Cleanup | Owned worker/Host exits, no pending resources/operations, retained failure evidence | No resources created by offline delivery |

The existing P1500 firmware 4.0 family in the [expansion plan](../plans/expansion-plans/opc-ua-engineering.md) is only a candidate first fixture. Bind an actual selected CPU; do not advertise support for that family from its documented catalog presence.

## UA-03 implementation and bounded native sequence

1. Add an owning-worker read-only adapter with exact native identity resolution and bounded owner traversal. Reuse the current process-lifetime controller; do not reuse historical admission or patch generated PLC objects. Integration owns Host/worker dispatch and catalog edits. Existing offline `opcua_inspect` is already defined and must keep its upload contract; choose a distinct name or existing exact-selector capability route for native eligibility.
2. Resolve exactly the admitted project, PLC and owner. Observe the documented `OpcUaProvider` service and supported category compositions without creating anything. Record service absence, wrong owner, unsupported firmware/product, disabled server, insufficient rights, license unknown, probe failure and not evaluated separately.
3. Capture bounded native descriptors and explicitly allowlisted nonsensitive values. Record unsupported or unreadable attributes with precise provenance. A descriptor or configured enum value does not prove entitlement or effective runtime behavior.
4. Inventory exact custom, reference and SIMATIC interface categories, retaining full identity and any unsupported/unknown category separately. An empty collection is distinct from a missing service. Refuse ambiguous selectors and drift against the observed snapshot.
5. Export only the previously selected existing interface to a newly allocated Host-owned artifact destination. Recheck selected identity immediately before the call. Refuse a preexisting destination, category/method mismatch, stale source, absent source or unexpected side effect. Native export is an explicit artifact write; it is not an implicit fallback within offline analysis.
6. Independently inspect the raw exported XML and hash, retaining opaque mappings/extensions and diagnostics. Compare all recorded before/after source and peer evidence. Upload/capture the exact export through the offline workflow with native provenance separately retained; its generic parser success remains insufficient for Siemens dialect or native import support.
7. Close owned resources normally, perform fresh process readback when available, reconcile every reservation and retain exact exits. A lost export response must be reconciled by destination existence/hash and independent source observation; never automatically replay an unknown native outcome.

## Required negatives before a production proposal

| Case | Required outcome |
| --- | --- |
| Wrong or ambiguous project/PLC/interface | Refuse before export; preserve every project and peer |
| Missing provider or selected category | Exact unavailable/unsupported reason; no automatic creation or activation |
| Unsupported/unknown product, firmware, rights or license observation | Distinct bounded status; no inheritance from another profile |
| Disabled UA server | Record configured state; never enable it for discovery or export |
| Stale selected identity/source | Reject against independent readback; no path-following by short name |
| Existing output / unsafe path | Refuse, preserve original output hash; never overwrite |
| Malformed or oversized exported artifact | Keep exact raw evidence and error; do not label parser success or native model completeness |
| Export failure or lost response | Preserve diagnostics, reconcile exact output and source/peer state, record unknown when evidence is insufficient |
| Cleanup interruption | Owned resource disposition and pending operation remain explicit; no support promotion |

Passing this packet would qualify only its exact eligibility/export profile. A later UA-Q00 packet must independently settle native mapping extension grammar and version acceptance before any import; reference generation timing and options before generation; full preservation/reopen/recovery and negative cases before writes; security evidence before security provisioning; endpoint/runtime and client migration evidence before compatibility claims. The current offline implementation leaves all those flags false.
