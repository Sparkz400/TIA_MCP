# Structured-edit batches, compact HMI model, capture progress and contract resources (14 November 2026)

Single native runs on this workstation (TIA Portal V21) against owned fixtures; not curated catalog evidence.
Reports are retained in the main checkout under `artifacts/us-sb1/report.json` (batch) and `artifacts/us-hm2`
(HMI model).

## Structured block edits with items

`scripts/probe_structured_batch.py` on a fresh `tia_create_scratch` project: two copies of the typed LAD fixture
`Tgp_Boolean_LAD` (titles and comments stripped) imported as `BatchLadA` and `BatchLadB`, then one
`graphical_operand_set` change with two items setting Access 21 of network 3 from `A` to `Inhibit`.

| Check | Result |
| --- | --- |
| Two items naming one block | Refused at prepare: `InvalidRequest`, "items[0] and items[1] edit the same object" |
| Two items, two blocks | Applied with one checkpoint, one baseline compile, `batch_items_preflighted`, `batch_items_verified`, one compile (succeeded), `native_preservation_verified` and one save; two structured-edit readbacks |
| Operands afterwards | `Inhibit` in both blocks |

`call_parameter_set`, `graphical_network_patch` and `graphical_network_insert` use the same path and were checked by
managed tests only.

## HMI model size

Seeded Unified PC fixture (`HmiSeeder qualify ... unified-pc positive`), `hmi_inspect` section `model` on `HMI_RT_1`:

| Request | Rows | Payload |
| --- | --- | --- |
| Default | 99 | 176,442 bytes |
| `excludeSystem` | 45 | 96,515 bytes |
| `rowMode: compact` | 99 | rows without identity and attributes |
| `excludeSystem` + `compact` | 45 | 22,780 bytes |

`sectionContentSha256` was equal between full and compact reads of the same roots.

## Not covered natively

Stage progress notifications and `remoteAccess` in `project_snapshot_capture`, and the `tia://contracts` resources,
were checked through the Host only (resources: 157 contracts listed, `change-kinds.v1.json` read, names completed, a
path outside the contracts refused).
