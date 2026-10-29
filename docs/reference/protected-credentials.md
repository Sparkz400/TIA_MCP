# Local protected credential references

The Windows Host provides local credential commands without Siemens installed.
They store bounded opaque bytes with Windows DPAPI CurrentUser and return public,
context-bound references. There is no MCP secret-input/export tool, live provider,
password policy or claim that a PLC/server accepted the credential.

## Create and inspect

Create `binding.json` containing public exact selectors, not credentials:

```json
{
  "resource": {
    "provider": "example-provider",
    "authority": "example-authority",
    "ownerId": "exact-owner-id",
    "resourceKind": "account",
    "resourceId": "exact-account-id"
  },
  "purpose": "runtime-authentication",
  "subjectId": "exact-subject-id"
}
```

These are illustrative identifiers, not discovered Siemens identities. A future
trusted provider must authenticate the authority and canonicalize aliases using
its domain policy. Case and exact strings matter; names alone do not establish
native authorization. The [public contract](../../contracts/protected-credential.v1.schema.json)
rejects extra fields, unknown shapes and invalid identities. Public selectors are
not a secret classifier: never put a password/token/key in them.

Run from a local console (adjust the installed Host path):

```powershell
$stored = & ./host/TiaMcp.Host.exe --credential-store create --binding ./binding.json | ConvertFrom-Json
$stored.reference | ConvertTo-Json | Set-Content -Encoding utf8 ./reference.json
& ./host/TiaMcp.Host.exe --credential-store inspect --binding ./binding.json --reference ./reference.json
& ./host/TiaMcp.Host.exe --credential-store list
```

The prompt does not echo characters. Backspace edits; Escape or Ctrl-C cancels.
There are no secret argument, environment-variable or file-path options. Input
is at most 65,536 bytes. Public binding JSON is bounded to 16 KiB and reference
JSON to 4 KiB; the complete encoded private record is bounded to 128 KiB. For trusted local automation, explicitly add
`--stdin` and write exact bytes to the process input pipe, then close the pipe.
The command cannot certify the source behind a redirected stream. No newline is
removed and binary bytes are preserved; ordinary shell string pipes
may append a newline and therefore change a password. Redirected input without
`--stdin`, empty or oversized input, duplicate/unknown options and malformed
public JSON refuse. UTF-8 public JSON may include a single BOM. Do not use shell
literals, transcripts or temporary plaintext files to supply secrets.

Status includes the public binding/reference, `active` or `retired`, protection
method, `secretReturned: false` and `decryptionVerified`. Only an active inspect
sets the last flag after a real DPAPI decrypt whose buffer is cleared. Create/list
make no readability claim. `list` can recover published public references after
stdout/reply loss; it does not decrypt. Unknown/damaged records refuse rather than
becoming an empty list or a newly generated credential.

## Generations and retirement

A repeated create produces a new independent reference. It never overwrites the
previous encrypted generation or changes an external password. Preserve the prior
reference while an external change or recovery is unresolved. To disable one
reference locally:

```powershell
& ./host/TiaMcp.Host.exe --credential-store retire --binding ./binding.json --reference ./reference.json
```

Retirement is idempotent, persists across processes and leaves ciphertext intact.
It is not server revocation, external rotation, secure erasure or proof of rollback.
The required lifecycle record is atomically replaced using the existing bounded
journal replacement helper. Missing, unreadable or unknown lifecycle state refuses
use; it never defaults to active. Ciphertext and public identity are immutable.
An interrupted creation may retain an orphan lifecycle or temporary encrypted
file, which authorizes no consumption. Orphans count toward the bounded store
capacity; they are not silently adopted or discarded as recovery evidence.

## Ownership, storage and recovery

The CLI stores data in `%LOCALAPPDATA%/TiaMcpProtectedCredentials/default`, outside
the normal artifact/bundle root. `--vault-name` accepts `default` or a 32-character
lowercase hexadecimal namespace, useful for an isolated installation or tests.
A missing Windows profile refuses rather than falling back to the working folder.
Windows application/package filesystem redirection can place this logical folder
in a different physical cache. The resolved physical path is part of the binding:
create and consume in the same execution context. Moving between packaged and
unpackaged contexts is not a qualified migration and may expose a different store.
Directories/files are created with protected current-user-only ACLs. Every operation
rechecks ACL ownership/inheritance/rights and rejects unsafe ACLs, network aliases,
reparse paths and multiply linked files. Existing unsafe ACLs are never repaired
implicitly. A shared OS file lock coordinates processes using that store.

DPAPI entropy binds the canonical vault path, Windows user SID, vault ID, credential
ID and canonical public binding hash. References additionally bind ciphertext
hashes, never plaintext hashes. Changing metadata and recomputing public hashes
cannot change the DPAPI binding. Copying a store to another path is not a supported
migration. Keep the complete store (header, lifecycle and encrypted records) and
original usable Windows profile in any private recovery strategy; ciphertext by
itself does not prove recoverability. Missing metadata never initializes a new
identity over existing files. Profile/key loss, changed permissions or ciphertext
integrity failure refuse with sanitized errors, without plaintext fallback.

This does not defend against the Windows account owner, an administrator or code
running with equivalent access. Such actors can access current-user DPAPI or replay
backups; ACLs are not a remote-provider authentication mechanism or an anti-rollback
hardware counter. No automatic migration, cross-user recovery or plaintext export
is implemented. Preserve unresolved external obligations in the
[engineering journal](engineering-operation-journal.md#external-resources-wa-09-portable-portion);
a readable local credential or restored project cannot satisfy endpoint/trust proof.

## Operation ownership and single dispatch

An immutable engineering plan may carry `credentials`: bounded named slots with
`dispatch` or `recovery` purpose, exact public reference and independently expected
binding. All slots use one vault, and each external account/resource must be
covered by an exact resource or whole-owner reservation. Old plans omit the field,
which preserves their original bytes and hashes.

The trusted Host admission path verifies all current and recovery references and
persists private operation pins before journal acquisition. Each pin identifies the
canonical artifact root, operation ID and immutable plan hash. Pins stay in the
required lifecycle file under version 2; missing pins cannot default to unreserved.
Interrupted pin publication does not authorize dispatch. Retrying acquisition is
idempotent; stopping its prepared/acquired journal releases that obligation.

Local `inspect` and `list` expose these public `operationReferences`. Retirement
reads every referenced journal through its full transition/evidence validator.
Prepared, acquired, checkpoint, dispatch, unknown and recovery states refuse with
`CredentialInUse`; unavailable/corrupt journals refuse with
`CredentialReservationUnresolved`. Retain those artifact roots. Only stopped,
succeeded or independently reconciled operations release the reference, and pin
history and ciphertext survive retirement. A terminal credential-bearing operation
cannot reopen for recovery: prepare a fresh plan with usable references.

`AcquireOperation`, `BeginOperationCheckpoint` and `DispatchOperation` hold the
vault OS lock before the journal lock. Revalidation callbacks receive a frozen
record and never recursively read the journal. Dispatch rechecks all pins and
readability plus the existing exact native target/session/content requirements,
rejects malformed Unicode in session/owner/native identity and probe names before
JSON can replace it, persists the existing one-use `dispatchIntent`, then invokes one synchronous trusted
consumer with only the named dispatch slots. Borrowed buffers are cleared on return
or exception. A consumer failure is sanitized and records `outcomeUnknown` when
possible; persisted intent alone still holds ownership if that write fails. A lost
reply cannot replay the consumer. Returned success is only dispatch intent;
independent readback, preservation and other required gates remain mandatory.

The raw `Use` seam refuses any operation-pinned reference, even after its operation
finishes. Core journal calls carrying references require a trusted credential
verification callback. These callbacks are an adapter trust boundary, like native
reacquisition, not authorization supplied by an MCP caller. Providers must still
hold their native exclusive-access lease across validation and actual invocation.

### Fresh recovery consumption

`RecoverOperation` uses the existing recovery intent once. A trusted adapter supplies
an explicitly reviewed recovery observation and reacquires it under its exclusive
native lease. The exact object, canonical project path, hardware/native owner,
adapter/profile and TIA version must still match the original plan. The recovery
review may have a new session epoch and changed source/content hashes; the fresh
reacquisition must match that review and observe every required probe. The fresh
observation is retained as `recoveryTarget` before any consumer receives bytes.
This is a trusted adapter contract, not an authentication claim from public strings.

Only `recovery` slots reach the recovery consumer. All references must still have
valid active lifecycle pins in the same vault, but dispatch-only ciphertext is not
decrypted: losing the newly used generation must not disable a separately readable
prior generation. Missing recovery bytes or pin metadata refuse before reacquisition.
Borrowed recovery buffers are cleared on return/error, and exception text is sanitized.

External-only recovery can have zero local restore destinations and does not require
an unselected local archive to be readable. Its external baseline and credential pins
still must validate; selecting a local restore requires that exact checkpoint. Its retained target
still records first use and prevents replay after a lost reply or unknown outcome.
Combined recovery reserves explicit new local destinations in the same journal.
No later evidence revision may add a destination or replace/remove the recovery
context. A returned consumer leaves `recoveryIntent`; a failed consumer records
`outcomeUnknown` when possible. Neither releases any original resource or credential.
Independent reconciliation must account for every original resource and each admitted
local destination; project restoration alone never clears an external effect.

Historical credential-bearing recovery records with an already admitted local
destination remain readable without inventing a recovery context. They cannot be
consumed again. Fresh credential recovery admission always requires the reviewed and
reacquired context. Old records without credential requirements retain their previous
serialization and recovery behavior.

Native providers, authenticated external target association, profile/context migration
and recovery against a different or inaccessible original owner remain unqualified.
The current same-owner context must not be weakened to infer a mapping to another
project/object or changed binary/profile. No new public mutation tool or native
provider is enabled by the portable tests.

## Integration and verification boundary

`CredentialVault.Use` is an internal synchronous trusted-consumer seam. It validates
an independently expected binding, refuses pinned/retired/unavailable state and clears
borrowed bytes even when a consumer fails. Consumer failures are sanitized. It is
not a public MCP method. The only native consumer is the
[change apply credential route](#change-apply-credential-route) below; the runtime
Web API and OPC UA logins use the seam inside the Host.
Existing generated scratch CPU credentials keep their separate native format and
restrictions. Do not pass these references as scratch simulation metadata.

## Change apply credential route

Block protection changes (`block_protect`, `block_unprotect` and
`block_write_protection_set`, see [engineering workflows](../guides/engineering-workflows.md))
take a vault secret through this route. No other path hands a vault secret to a worker.

**Create one credential per block and protection.** The worker derives the expected
binding from the exact target; copy `plcPath` and the block `path` from `project_inventory`:

```json
{
  "resource": {
    "provider": "siemensTiaPortalV21",
    "authority": "/projects/P/devices/PLC_1/items/PLC_1/plc/PLC_1",
    "ownerId": "/projects/P/devices/PLC_1/items/PLC_1/plc/PLC_1/blocks/FB_Motor",
    "resourceKind": "blockProtection",
    "resourceId": "knowHow"
  },
  "purpose": "blockProtection",
  "subjectId": "blockPassword"
}
```

`resourceId` is `knowHow` for know-how protection and `write` for write protection.
Enter the password itself, without quotes: 1 to 4,096 bytes of UTF-8, stored exactly.
Protect and unprotect of one block use the same credential. A write-protection
password change needs a second credential with the same binding (`newCredential`).
A renamed or moved block, or a project copied under another name, needs a new
credential, because the binding names the paths.

**Prepare** names the slots. The preview lists each slot with its `vaultName`,
reference and expected binding; no secret is read.

**Apply.** The worker returns the same public slots when it opens the shared mutation
lease. Before admission, the Host checks each slot: the purpose is one it routes, the
reference's `bindingSha256` matches the binding, the record exists, is active and not
pinned to an engineering operation, the store is free, and the secret is UTF-8. Under
the vault lock it decrypts the secret and immediately seals it again with DPAPI
CurrentUser. The entropy binds the lease token, change ID, preview hash, slot,
credential ID and binding hash. The plaintext buffer is cleared at once. Only the
`mutation_dispatch` of that lease carries the sealed envelopes, over the Host-created
current-user pipe whose peer process is verified. The worker opens each envelope into a
read-only `SecureString` before the checkpoint and once more for the native call, and
clears the envelopes when the dispatch returns. A new lease cannot open an old envelope.

| Refusal | When | State |
| --- | --- | --- |
| `CredentialContextMismatch` | The reference is bound to another target, purpose or protection; `details.expectedBinding` names the binding. Also when an envelope does not open for this apply. | Before admission (worker case: before the checkpoint) |
| `CredentialUnavailable` | Record missing, unreadable or not decryptable | Before admission |
| `CredentialRetired` | Reference retired | Before admission |
| `CredentialAdmissionRequired` | Reference pinned by an engineering operation | Before admission |
| `CredentialStoreBusy` | Another holder keeps the vault lock past the bounded wait | Before admission |
| `CredentialFormatInvalid` | Secret is not 1 to 4,096 bytes of strict UTF-8 | Before admission |
| `CredentialRouteInvalid` | Malformed or duplicate slots, slots in more than one vault, a purpose the Host does not route, or slots on an operation other than `change_apply` | Before admission |
| `CredentialRequired` | The worker received no envelope for a slot | Before the checkpoint |
| `CredentialPolicyViolation` | TIA's password policy refuses the password (V21: 8 to 120 characters with a digit, a special character, upper and lower case) | In the native call, after the checkpoint; the change is `recovery_required` and the block unchanged |

A refusal before admission leaves no admission record, and the change stays `prepared`.
TIA checks its password policy only in the native call, so create credentials that meet it.

**What is retained.** Preview, change journal, admission record and responses hold only
the reference and its public binding. The checkpoint archive is TIA's own archive of
the project before the change. After `block_protect` it holds the unprotected block.
After `block_unprotect` it holds the block still protected with the same password.
TIA keeps its own protection data in the project; no Host secret or envelope is in
it. `change_restore` needs no credential. The restored project keeps its name and
paths, so unprotecting the restored block again takes the same credential reference.
An interrupted apply is reconciled without the secret, and the single-use lease means
a lost reply cannot replay the native call.

**Limits.** Like the vault, the route does not defend against the Windows account owner
or an administrator. Retiring a credential after the lease opened does not recall the
envelope already sealed for that one apply. The CPU protection level's full-access
password can use the same route: the worker names a `cpuProtection` slot for the
exact CPU and the Host adds that purpose to the ones it routes. That is not implemented.

The Host uses Microsoft's
[System.Security.Cryptography.ProtectedData 10.0.13](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/10.0.13).
The [DPAPI sample](https://github.com/dotnet/docs/blob/main/standard/security/snippets/how-to-use-data-protection/net/csharp/sample.cs)
and [ACL-at-creation API](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemaclextensions.create?view=net-10.0)
underpin the platform usage. Package metadata, license and notices are retained in
`third-party/` and included in portable bundle verification.

Managed tests exercise actual local DPAPI/ACL and separate processes; the CLI harness
uses disposable random synthetic secrets over redirected stdin. Fake key sequences
cover console input editing/cancellation; physical-console acceptance, a different
Windows user, lost/roamed profiles, real key/password recovery and Siemens/native
provider acceptance remain unexecuted. See the current [execution queue](../history/offline-execution-20261016.md).
