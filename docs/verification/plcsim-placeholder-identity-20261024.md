# PLCSIM all-zero discovery entry identified — 24 October 2026

## Result

The all-zero `PN/IE / PLCSIM (1)` entry (`Accessible device`, address and MAC
`00-00-00-00-00-00`, empty device series) comes from PLCSIM Advanced's own Softbus
endpoint. It is not a simulated CPU. It appears once the Simulation Runtime API is
initialized through `SimulationRuntimeManager.Version`/`NetworkMode`, with zero
registered instances. It stays visible to every Portal, including brand-new ones,
for as long as that Runtime Manager process runs.

These factors do **not** produce the entry:
- a running Runtime Manager started by `RegisteredInstanceInfo` alone;
- a Portal that was already open when the manager started;
- an offline CPU in the project.

Before the API was initialized and after `Shutdown`, the route is absent.

This matches the reviewed placeholder rule, which excludes at most one exact all-zero
entry bound to the exact Runtime Manager process while zero instances are registered
(see the [guard review](plcsim-placeholder-guard-independent-review-20261023.md)).
It also explains the frozen run: every case showed one such entry and excluded it
([frozen run](runtime-frozen-20261024.md)). It explains historical attempt 04 too,
where the pre-rule guard refused startup on it. The isolation guard is unchanged:
every other nonempty snapshot stays significant.

## Method

`TiaMcp.Openness.V21.exe --plcsim-placeholder-identity-probe DIR`
(`src/TiaMcp.Openness.V21/PlcsimPlaceholderIdentityProbe.cs`) runs alone under the
native lane lock. Each phase starts a fresh headless Portal and creates an empty
project. It takes three `StationUploadProvider` snapshots one second apart and
records the Runtime Manager process IDs before and after. The probe requires zero
registered instances. It never registers, starts or changes a simulated CPU, and
reads no credential.

| Phase | Runtime Manager | Route | Entries |
|---|---|---|---|
| a, before initialization | none | absent | — |
| b, initialized by `RegisteredInstanceInfo` | running | absent | — |
| c, after `Shutdown` | none | absent | — |
| d, reinitialized by `RegisteredInstanceInfo` | running (new PID) | absent | — |
| e, Portal open first, then `RegisteredInstanceInfo` | started during phase | absent | — |
| f, fresh Portal, offline CPU 1511-1 PN in project | same as e | absent | — |
| g, Portal open first, then `Version` + `NetworkMode` (`00080000`, `Softbus`) | started during phase | present | one all-zero |
| h, fresh Portal, offline CPU, `OnlineProvider.Configuration` also read | same as g | present (both providers) | one all-zero |

All three snapshots in each phase agree.

## Evidence

Under this checkout's ignored `artifacts/` directory:

- `ph1/result.json`, phases a–d (sha256 `5996874679a245ed6f83e86e2c87d5023a318d02030acc07cadd73efe8606341`).
- `ph2/result.json`, phases a–f (sha256 `7d44e8a95334346a1ec00c39807358c515a6105a9c75fe2a463f2a52ab834671`).
- `ph3/result.json`, phases a–h (sha256 `c389a5942533321531fe78bf6f6809a6a02809fd7d7ff6c4a2198b6f12091fd4`), probe commit `240e61a`.

Every run completed with exit code 0. The phases that the later runs repeat
gave the same results.

## Limits

- `Version` and `NetworkMode` were read together, so which of the two announces
  the endpoint is not separated. The rule does not depend on that.
- This was one PC, PLCSIM Advanced V8 and TIA V21, in Softbus mode. Other
  Softbus participants, such as other simulator products, were not present.
- The probe observes; it does not change the production rule or its tests.
