# Library dependency fixture

Original SCL/UDT documents without native GUIDs:

- `TiaMcpDepInner.udt` ← `TiaMcpDepOuter.udt` (a nested member and an `Array[0..2]` of Inner) ← `TiaMcpDepChild.scl` (input of Outer) ← `TiaMcpDepParent.scl` (Child multi-instance and an Outer input).
- `changes/TiaMcpDepParent.scl` is Parent 1.1.0 without the Child multi-instance: a reference removed by an update.
- `ConsumerReferences.scl` calls two Parent instance DBs from `Main`.
- `collision/` holds a same-name Inner with different content (authored as a separate library type, so a different GUID) and a same-name unlinked Outer.

`scripts/qualify_library_dependencies.py --run-native` creates and releases all four types in an owned scratch project and compares the native closure with the graph above. Publishing only Parent must carry its exact closure. In a fresh consumer it instantiates Inner alone, then Parent: the preview must reuse exactly the installed Inner and create only Child, Outer and Parent. Further steps: two instance DBs, an exact no-op update, refusal of a non-default older version, refusal of the 1.1.0 update that strands Child, a copied newer project default and refusal of the downgrade below it. A third scratch project checks both collisions. A fresh reopen confirms the saved links and default. Refusals must leave the PLC inventory unchanged.
