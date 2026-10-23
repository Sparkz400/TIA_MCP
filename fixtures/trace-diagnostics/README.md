# Offline conveyor diagnostic fixture

These are **authored XML fragments and synthetic model/measurement observations**, not native exports, compilable qualification sources, or executed Siemens evidence. The fragments exercise the documented interface and explicit CallInfo shapes used by the bounded reader. The test constructs a saved review with an explicitly synthetic capture marker and a simulated native-confirmed type edge.

`DB_Line.Conv1` and `DB_Line.Conv2` instantiate the same `FB_Conveyor` through `FB_Line`; `Main` calls the root instance. `DB_IO` supplies independent command/feedback/interlock/final-output/writer-guard roles. `FB_Conveyor` has state, elapsed time, timer completion, fault and an unavailable TEMP member. The separate writer is represented by the authored measurement oracle; its program semantics are **not** parsed or proved by this fixture.

The independent oracle in `expected-mappings.json` names the intended second conveyor, its declaration paths, and an injected event where the internal run command remains true while final output falls for three 10 ms sample intervals and the writer guard is true. These intervals are synthetic and establish no target sample rate. Tests also use the first conveyor, ambiguous names, missing calls, duplicate scopes, protected content, unavailable members, stale reviews, polling, missing values, timestamp gaps, no fault and tampered artifacts.

Native fixture authoring, compile/reopen, live settings readback, genuine injected acquisition, interruption/recovery and existing trace preservation remain separate admission-controlled work.

The expanded managed tests add independently authored in-memory SCL assignments, explicit actual arguments, a separate guarded writer and a FlgNet two-endpoint wire. These test conservative dependency and binding extraction; they do not upgrade the base fixture into a native project or prove runtime causality. Other cases supply synthetic CSV/readback evidence, negative pre-trigger times and explicitly mapped before/after instances. No actual target or native measurement is represented by those artifacts.
