# Protected OPC UA client identity

The Host now supports an explicitly created, named Windows current-user identity
through `--ua-client-identity`. It persists the client certificate and application
URI without persisting session tokens or resuming acquisition. Existing launches
retain ephemeral behavior. See the [setup and rotation workflow](../reference/opcua-runtime.md#protected-persistent-client-identity).

The implementation reuses the credential vault's strict ACL/path validation,
bounded JSON, exclusive OS lock and atomic publication. DPAPI encrypts the private
PFX with context binding; loading uses an ephemeral key import and clears decrypted
buffers. One Host owns each identity. Public export is create-only. Rotation
requires the current SHA256 and an idle identity, keeps the application URI and
requires explicit new server trust. Missing or damaged stores never bootstrap
replacement keys. There is no new dependency, MCP mutation tool or native worker.

## Local evidence

- Protected credential suite: 101 checks, plus its existing 81 operation-ownership
  and 106 recovery checks. New identity coverage includes fresh-process restart,
  actual owner process loss, concurrent ownership and rotation refusal, exact
  private/public key possession, stale-fingerprint refusal, failed atomic
  replacement, corrupted ciphertext/public metadata, missing records/headers,
  strict ACL and hard-link rejection, moved-store refusal, expired/not-yet-valid
  certificates and explicit renewal.
- Encrypted loopback runtime suite: 602 checks, including exact array/matrix
  values across reads, polling, subscriptions, retained reopen and exports.
- Actual Host MCP: 124 checks. CLI creation/inspection/public export, same-identity
  restart under a new Host owner, unchanged server trust, protected-key handshake,
  no session adoption, blocked in-use rotation, explicit rotation, server refusal
  before new trust and success after explicit test-server trust provisioning.
  Original process exits and source/binary hashes are retained by the harness.
- Focused OPC UA Python tests: 26 passed.

Receipts are preserved at
`D:/Entwicklung/TIA_MCP-public/artifacts/opcua-identity-20261025`.
The test server is synthetic and binds only loopback sockets. The test identity's
randomly named protected directory is removed after all owned processes exit;
the public certificate and validation receipts remain.

## Packaged array polling regression

A full hosted package run after PR #58 failed when the array test attempted to
parse a polling `gap` row's null `readResultJson`. Gaps are legitimate when the
single sequential read cycle takes longer than its interval; production already
retained them explicitly. The test now selects read rows, requires all expected
successful values, and preserves/verifies gaps in export/reopen checks. A bounded
test-server delay ensures a gap occurs in every run, so this is a deterministic
regression check rather than a timing-dependent rerun. No production capture
semantics or failure rows were weakened.

These checks do not qualify a physical PLC, Siemens configuration, another Windows
user/profile, hardware-backed key isolation, CA enrollment, revocation management
or migration to another machine. Programs running as the same Windows user remain
inside the DPAPI trust boundary. Native and release acceptance remain separate.
