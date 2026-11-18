# WinCC Unified runtime alarms

The `runtime_unified_*` tools read and acknowledge alarms of a running WinCC Unified PC Runtime
through its GraphQL server, as the WinCC Unified GraphQL System Manual (11/2025, installed with
WinCC Unified Runtime under `Documentation/English/GQLWCCUenUS.pdf`) describes it. They are in the
`runtime` tool group. Not run against a live runtime yet ([TODO](../TODO.md#user-feedback-on-v090-18-november)).

## Connecting

The server is reached only through the runtime's IIS web server at `https://<host name>/graphql/`.
The manual forbids `localhost` and `127.0.0.1`, so the tools refuse them; use the host name the
Runtime web server certificate names.

1. `runtime_unified_credential_binding` with the endpoint, the lowercase SHA-256 of the web server's
   leaf certificate and the UMC user.
2. Store the password once with the local credential CLI, as a JSON string
   (`TiaMcp.Host.exe --credential-store create --binding <binding.json>`, hidden prompt or `--stdin`).
   It returns the protected reference; no tool accepts a password or token.
3. `runtime_unified_connect` with a new 32-hex `attemptId` and the login (endpoint, pin, user,
   `vaultName`, credential reference). The certificate must match the pin, the host name and its
   validity dates. The login reads the user's UMC language and, by schema introspection, which
   alarm fields the server offers.

Sessions live in the Host's memory. The server ends an idle session after the user's
`autoLoggOffSec`; connect again with a new `attemptId` after `UnifiedSessionExpired`. A failed
login reports a code only, never the server's text, since that text could echo the password.

The user needs the function right *GraphQL - read access* to read alarms and *GraphQL -
read/write access* to acknowledge them.

## Reading alarms

`runtime_unified_alarms` with `kind: active` (the default) or `logged`.

- **Languages:** `languages` defaults to the UMC user's language. The server's own default is
  `en-US`, which fails on a runtime without English (a de-DE runtime's GraphQL server stopped on it),
  so the tool never leaves it to the server. When the user has no UMC language, pass `languages`
  (for example `["de-DE"]`); codes are case-sensitive.
- **Filters:** `filterString` is the manual's QCL `WHERE` part, for example
  `path LIKE '*::Tag01:*'` or `priority > 10`. The server cannot filter by `state`,
  `stateMachine`, `changeReason`, `sourceType` and a few display properties.
  `filterLanguage` defaults to the first requested language.
- **Rows:** translatable texts (`eventText`, `alarmText1`, `stateText`) are keyed by language.
  Logged alarms take `startTime`, `endTime` and `maxItems`, and have no `path`.

## Acknowledging

An alarm's `name` is only the configured alarm name (`Alarm_Tag01`). `acknowledgeAlarms` needs the
fully qualified name, which active alarms report as `path`: `System::Tag:Alarm`, or
`System::Tag.Element:Alarm` for an element of a structure or array (`HMI_RT_1::Tag01:Alarm_Tag01`).
Every active row therefore carries `acknowledge: {path, instanceID}`.

`runtime_unified_alarm_acknowledge` takes 1–64 of those. `instanceId` 0 (or none) acknowledges every
active instance of that alarm. A plain name is refused with `UnifiedAlarmPathRequired`. Because
acknowledging changes the runtime's alarm state, it also needs:

- `acknowledgeWrite: true`, given only after the user asked for it;
- an endpoint whose host name resolves only to addresses of this PC (`UnifiedWriteRouteRefused`
  otherwise), so only the Unified PC Runtime on the same machine is acknowledged.

The result lists the server's rows with their item error codes: 2 (name not resolved), 304 (invalid
object state), 305 (not acknowledgeable in the current state). Reset, shelve, enable and disable are
not exposed.
