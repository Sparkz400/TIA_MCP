# Library observation contract fixtures

`component-plan.json` is a reserialized preview from the original component05
report, retaining the proposed two-step installation, dependency exports, explicit
content policy and observed mappings. It does not change that run's outcome or
authorize combined apply. [Original pointer and scope](../../../docs/verification/library-component-previews-20261023.md).

`hmi-binding-change.json` is an authored synthetic Unified faceplate-binding
change. It exercises selected-version, captured interface/tag and retained-layout
comparison, without a native capture or qualification claim. Its provenance and
scope are recorded in [HMI preview verification](../../../docs/verification/library-hmi-previews-20261023.md).

These authored identity, lock receipt, plan and change shapes are synthetic. They exercise
serialization contracts and grant no native authority. Native project/library
paths and process IDs are illustrative; the validator never opens referenced
paths. Operation-specific result and compilation payloads remain opaque evidence.
Preview hash strings are illustrative; the local validator compares repeated pins
but does not recompute the producer's Newtonsoft preview hash. The plan/change
validator checks repeated structure and conservative recovered-state consistency.

Separate verification checks original, hash-pinned historical native receipts;
those observations remain scoped to their original source and target profiles.

`hmi-targets.json`, `hmi-inventory-classic.json` and
`hmi-inventory-unified.json` are synthetic examples of the current library HMI
query response shapes. They do not qualify native inventory behavior.
`hmi-capabilities-missing.json` and `hmi-capabilities-installed.json` preserve
original metadata-only worker responses for absent/installed optional API files.
The probe blocks optional WinCC assembly loading and checks that no Portal
instance is acquired. It does not establish product licensing or native workflow
acceptance. See [capture provenance](../../../docs/verification/library-hmi-query-20261023.md).

`compiler-legacy.json` is a derived structure from the original retained native
library lifecycle report, with new serialization bytes. `compiler-device.json`
adds explicitly synthetic device identity to that diagnostic fixture;
`compiler-hmi.json` is synthetic. Their compiler counts are not counts of
Warning/Error rows: native ancestor messages may repeat a diagnostic severity.
See [compiler provenance and limits](../../../docs/verification/library-compilation-observations-20261023.md).

`update-plan.json` is a reserialized project-update preview extracted from the
same hash-pinned lifecycle report. It retains the affected block, an unrelated
library type, comparisons, baseline and OB-rooted cleanup graph. The original
report is unchanged; the fixture has distinct derived bytes. Provenance and
validation limits are recorded in [update analysis verification](../../../docs/verification/library-update-analyses-20261023.md).

`synchronization-change.json` is a reserialized applied publication response from
the same retained lifecycle report. It records promotion from version 1.0.0 to
1.1.0 while preserving both releases. Its original source pointer and derived
bytes are pinned in [synchronization verification](../../../docs/verification/library-synchronization-20261023.md).
