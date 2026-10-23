# C01 bounded component-to-text-list generation

The existing `hmi_text_list_author` endpoint now accepts the strict v2 request
described in [the reference](../reference/unified-text-list-authoring.md#component-bound-generation-and-regeneration-v2).
It derives one plain Unified Bool text list and its TextLibrary companion from
an exact v7 saved component/output/binding selection and explicit translations.
The implementation reuses the existing v1 YAML author; no new native format,
SiVArc API, native mutation or qualification path is introduced.

The retained workflow covers initial generation, changed bilingual content and
explicit renamed-Bool-member regeneration. Updates require independently supplied
complete baseline/current pairs matching the retained predecessor's generated
bytes. Manual edits, missing files, incomplete coverage, manual/unknown ownership,
changed type, removed members, foreign before-component lineage and preserved-peer
filename collisions suppress both candidates and retain their conflicts. Selected
manual peers remain opaque exact source bytes. Relevant dependency contradictions
remain blocking; broader unrelated conflicts and incomplete coverage do not
silently become selected-output conclusions.

Evidence is under `artifacts/c01-generation-20261020/`:

- `focused-04/text-generation/verification.json`: all 71 managed checks pass,
  including 16 allowed predecessor steps, refusal beyond the lineage limit,
  v1 predecessor compatibility with an explicit missing-lineage gap, upload
  release, fresh store reopen, original/predecessor/manual-source tampering and
  rehashed forged qualification, intent, lineage and preserved-file descriptors.
- `mcp-02/verification.json`: all 34 actual MCP checks pass across two clean Host
  lifetimes. Fresh uploaded install/update/renamed-member requests generate the
  expected pair, uploads are released, conflict records reopen identically,
  original manual content survives unchanged and rehashed/tampered records refuse.
  Both lifetimes exit without forced termination or native worker execution.
- The independent Python oracle reads original component-review requests and
  original saved PLC XML, resolves the selected member, derives expected documents
  from the template and output name, and compares complete YAML trees. It verifies
  language ordering, Unicode and multiline text, exact integer/Boolean types,
  predecessor/current byte agreement and every selected retained peer. Altered
  translation content with an honestly recomputed digest still fails.
- The focused Python author/generation suite passes nine tests. The isolated
  Release Host build succeeds with zero warnings and errors; the MCP evidence
  retains exact Host/Core binary hashes and harness hash.

An early managed attempt exposed a wrong selector for the saved component summary
(`reviewId` lives under `selection`). It was fixed before the passing evidence.
A second test attempt exhausted the transient upload count during chain creation;
the producer now releases uploads immediately after their retained review/author
record succeeds. These attempts remain separate evidence directories and are not
represented as successful runs.

Historical v1 authoring construction and record serialization are unchanged.
The existing managed author tests passed before the new generation checks in the
initial aggregate attempt. The final combined batch's aggregate/workstation
validation is recorded separately; this note does not claim a new complete-suite
or native qualification run.

C01 remains open for additional templates/formats, full member/dependency/consumer
coverage, native instance/address association, ownership evidence and project
preservation. This profile does not merge current YAML, rename/delete outputs,
activate project languages, import candidates, establish native range acceptance,
compile/reopen/recover a project or validate HMI visuals/runtime. The saved
component observations in this test are synthetic. All native, ownership,
preservation and complete-consumer qualification flags remain false.
