# Fifth parallel increment — 21 October 2026

The user authorized this complete bounded batch after reviewing the proposed
three-worker/coordinator plan. Base commit: `0a81f8537e5b7c57a522a149a13d043271bb57bc`.
Each lane uses its own Git worktree and build outputs. The coordinator integrates
the reviewed lane commits, owns shared catalog and validation wiring, freezes
the combined source and runs aggregate and relocated-package validation.

| Lane | Deliverable | Required acceptance | Separate obligations |
| --- | --- | --- | --- |
| UA acquisition | Bounded polling capture on the existing secure owned OPC UA sessions, using existing durable capture primitives | Actual encrypted loopback server and Host; independent value/status/time expectations; bounded acquisition, gaps, stop, interrupted capture, restart/read/export without reconnect; old Web capture compatibility | Subscriptions require their own qualification; physical device, independent online program association and write/control remain open |
| HMI conversion | Explicit value-preserving integer conversion during retained Unified text-list regeneration | Exact captured before/after types and original state intervals; signedness/width boundary cases; overflow refusal; manual-edit conflicts; independent candidate parsing and prior original-record compatibility | Native installation, broader types/formats and runtime visuals remain open |
| Explorer relationships | Explicit provider scope, relationship traversal budgets and honest retained-scope coverage | Independent expected edges; unresolved endpoints, cycles and duplicates; visible depth/node/edge truncation; bounded scale measurements and browser checks | Native collection completeness and actual desktop host acceptance remain separate |
| Coordinator | Shared contracts, integration, acceptance/evidence reconciliation and package | Focused checks per lane, one combined frozen aggregate and relocated package; source/binary hashes and original process outcomes; preserve failures and prior evidence | Full 44-workstream release, hosted CI and native gates remain open |

The native review is read-only and bounded. The original `hmic20c` reporter
lifetime remains uncaptured; previous investigation already found inaccessible
Security events and no Sysmon Operational log. Repeating the same query is not
an unblocker. No new native dispatch, quarantine removal or retrospective
qualification is part of this batch. See the [guard evidence](../verification/native-dispatch-guard-20261020.md).

Completion requires delivering the three scoped workflows and their combined
validation, with each applicable acceptance item linked to retained evidence.
Unavailable native or physical evidence cannot be substituted with offline tests.
