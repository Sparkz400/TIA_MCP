# Offline local Git document revisions

The normal Host exposes twelve `vci_*` tools without Siemens products. They retain
uploaded document bytes in separate Host-owned Git repositories, review and commit
selected changes, preserve parent history and export an exact old revision. This
is the local Git part of D01. [Package manifests and three-way comparison](vci-package-workflow.md)
are also available from explicit supplied mapping evidence. Native VCI workspace
mappings, export/import into TIA and engineering qualification remain separate obligations in the [execution queue](../plans/offline-execution-20261016.md).

Configure one installed Git executable at Host startup:

```json
{
  "command": "D:\\path\\to\\TIA_MCP\\artifacts\\host-only\\bin\\TiaMcp.Host\\release\\TiaMcp.Host.exe",
  "args": ["--artifacts", "D:\\TiaMcpArtifacts", "--git-executable", "C:\\Program Files\\Git\\cmd\\git.exe"]
}
```

Without `--git-executable`, VCI calls return `GitNotConfigured`; other offline tools
remain available. MCP callers cannot choose an executable, shell command, remote,
existing repository or arbitrary destination. The existing
[restricted Git runner](vci-git-runner.md) pins the executable and rejects hooks,
config includes, aliases, remotes, attributes, submodules, links and unsupported
repository state. All returned native qualification flags remain false.

## Caller workflow

1. Upload nonempty raw files through `artifact_upload_begin`, chunk transfer and
   `artifact_upload_commit`. Retain each resulting `upload:<id>` handle.
2. Call `vci_prepare` with an explicit author, message and selected files. Omitting
   `parentRevisionRef` starts a new repository. Supplying a completed revision makes
   an independent candidate with the same Git history. No original revision changes.
   Optional `branchName` selects the initial branch in a new repository, or creates
   and selects a new unused branch at the exact parent commit in its private copy.
   Omission inherits the parent branch (or `main` for a new repository). Reusing
   the current branch is allowed; a different existing branch is never reset or
   checked out. Names are literal bounded ASCII Git branch names (180 characters
   maximum); case aliases and path-prefix collisions are refused.
3. Read `files.json` with `vci_artifact_read`, and inspect `diff.txt`. Git diff covers
   tracked changes only. Review new files and any exact raw contents through
   `vci_file_read`, using paths from the file manifest. All downloads include the
   complete file hash/length and accept offsets for at most 64 KiB per call.
   `receipt.json` downloads the exact receipt bytes whose digest appears in the
   record handle. Review summaries also expose the pinned Git executable hash;
   completed revisions include its reported version.
4. Submit the exact `previewRef` and reviewed `planSha256` to `vci_commit`. The Host
   reconstructs and verifies the plan, checks all bytes and Git state, writes durable
   intent, and publishes only the candidate's local branch. The returned revision
   handle binds its completion receipt, exact commit/tree and verified callback.
5. Use `vci_history` for the committed ancestry. Its documented NUL-delimited fields
   are commit, tree, parent commits, author, email and subject; rows end with LF.
6. Use `vci_branches` on a committed revision to inspect up to 50 local refs per
   page. Pass the returned `inventorySha256` as `expectedInventorySha256` on every
   nonzero-offset page. Changed inventories fail with `PreviewStale`; restart
   listing explicitly. These are fresh local ref observations, not remote state
   or part of the retained raw-document content. Non-commit-valued refs are rejected.
7. Use `vci_materialize` to recover any completed revision as a new raw-file directory,
   `revision.zip` and a hash/mode manifest. Download the ZIP with `vci_artifact_read`.
   Export does not reconstruct a TIA project or restore operating-system permissions.
8. Use `vci_reopen` and `vci_list` after Host restart. Completed candidates retain
   their own input bytes, so releasing the original upload does not break them.

Example preparation, using actual committed upload/revision handles:

```json
{
  "files": [
    {"relativePath": "PLC/Motor.s7dcl", "uploadRef": "upload:<committed-id>"},
    {"relativePath": "empty.txt", "uploadRef": null},
    {"relativePath": "obsolete.txt", "delete": true}
  ],
  "author": {"name": "Engineering reviewer", "email": "reviewer@example.invalid"},
  "message": "Reviewed motor document update",
  "branchName": "review/motor-update",
  "parentRevisionRef": "vci:revision:<id>:<receipt-sha256>"
}
```

A null/omitted upload with `delete=false` explicitly creates an empty file.
Deletion requires the exact file to exist in the parent. An upload and deletion
cannot be combined. Every companion file is an explicit selection. Use the package workflow to
review supplied mappings and required companions; it does not infer native
preservation or current target state from matching names or hashes.

## Interruptions and integrity

Each candidate is separate from its parent. Failed preparations remain discoverable
as incomplete records. A commit intent without a complete receipt remains
`outcomeUnknown`; `vci_commit` refuses a repeat, even if the last Git callback says
`verified`. Reopen exposes the validated callback phase, operation ID, candidate
commit/tree and callback digest for diagnosis.

When the final completion receipt alone was lost, call `vci_reconcile` with the
preview handle and exact `commitJournal.sha256`. It requires a retained `verified`
callback, unchanged executable, fresh matching HEAD/tree/index and exact retained
working bytes. It writes a missing receipt without running another Git mutation.
An earlier callback, changed repository, missing/corrupt callback or existing corrupt
receipt remains unresolved. The tool never deletes or overwrites evidence to make
recovery pass. A new candidate can still start from an intact prior revision.

Callbacks are flushed before each Git dispatch/publication stage and linked by
hashes. Completed receipts pin the final callback. File flushes and managed-process
restart checks are not proof of machine power-loss durability or protection from a
malicious operating-system writer. The Host serializes cooperating store operations;
the Git runner retains its own index and branch safeguards.

## Bounds and retained obligations

- 1–1,000 selected files, 16 MiB per raw file and 64 MiB per working tree. Git metadata
  copying is separately bounded to a 256 MiB repository, so an incompressible maximum
  raw file remains copyable when its compressed Git object is slightly larger.
- 128 retained records and a 2 GiB store with conservative headroom reserved before
  copying/committing; 4,096 retained process-control directories. Reaching a limit
  refuses new work. There is no automatic deletion of previous revisions or evidence.
- At most 200,000 filesystem entries per bounded scan, with no linked ancestors or
  descendants. Global store capacity includes failed candidates and retained controls.
- History accepts 1–50 commits and at most 64 KiB of text. Read/download pages are
  bounded to 64 KiB. Discovery lists at most 50 rows; relist after concurrent additions.
- Normal commit success is local Git evidence. Native package eligibility, VCI
  mapping, directed synchronization, native semantic validation, native project
  recovery, remote publication, larger measured scale and release acceptance remain open.

The [JSON contract](../../contracts/vci-workspace.v1.schema.json) defines the input
and output shapes. [Validation record](../verification/vci-host-workflow-20261016.md).

## Named-branch evidence and limits

Reviews expose `headReference`. Older retained reviews without this field keep it
null; their branch provenance is not invented. Forked reviews additionally retain
`fork-journal/intent.json`, `fork-journal/0001.json` and `fork-journal/0002.json`
as hash-bound downloadable artifacts. The fork starts at the unchanged parent
commit; the runner verifies source bytes, tree, clean index, configuration and
all pre-existing branch refs. Commit, history, restart reconciliation and exact
materialization then use the existing workflow on the candidate's named branch.

The implementation uses Git's non-forcing `checkout --no-track -b` with a literal
name and exact commit, hooks disabled and no remote access. See [Git checkout](https://github.com/git/htmldocs/blob/gh-pages/git-checkout.adoc) and [hook behavior](https://github.com/git/htmldocs/blob/gh-pages/githooks.adoc). No existing project or retained parent repository is checked out.
A failure after dispatch leaves only the private candidate uncertain. Preparations
with a fork journal but no completed review are listed as `outcomeUnknown`;
inspect retained files before disposal. There is no automatic fork replay or
repair. Machine power-loss durability is not qualified.

This delivers named initial/child branches and descendant commits. It does not
provide standalone ref reset/delete/rename/merge tools, remote tracking or
publication, or native VCI synchronization. Those broader D01 obligations remain
open. [Executed checks](../verification/vci-branches-20261016.md).
