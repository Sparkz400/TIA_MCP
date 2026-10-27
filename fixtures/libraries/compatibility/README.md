# Library compatibility fixture

`TiaMcpWideGate.scl` is an original FB with 64-bit types (`LInt`, `LWord`, `LTime`). It is authored as a library
type on the scratch CPU 1511-1 PN and then instantiated into an added S7-1200 CPU, to observe how V21 handles a
library type whose content the target CPU family may not support. The plain `TiaMcpLibraryGate` type from
`../type-lifecycle` is the control: it uses only `Bool`.

Run `scripts/qualify_library_compatibility.py --run-native` with exact `--host` and `--worker` paths and a short
`--artifacts` directory. It uses only owned headless TIA sessions and scratch projects.
