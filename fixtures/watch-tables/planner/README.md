# Watch planner fixture

`expected-motor.json` is a separately authored expected-entry oracle: MotorA Enable/Running/Fault/Starts in exact order, TEMP Scratch retained as a gap, and MotorB never selected implicitly. `tests/TiaMcp.WatchTables.Tests/Program.cs` constructs the captured evidence independently of this expected file. Literal digest values in that synthetic capture are test identities, not claims of native artifacts.

The same tests also consume genuine output of the existing offline DiagnosticPlanner fixture suite to verify adapter compatibility. Both sets remain synthetic offline evidence. No native watch/force table document, native grammar or PLC observation is represented here.
