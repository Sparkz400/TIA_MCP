# Related software-unit fixture

`UnitConsumer.scl` is imported into the consumer unit `TiaMcp_LibConsumer`. Its cycle OB calls the library FB and
uses the library UDT instantiated in the provider unit `TiaMcp_LibProvider`, through the consumer's `SoftwareUnit`
relation. `scripts/native/LibraryUnitSeeder related` creates both units and the relation; `publish` marks the
provider's library instances `Published`. Runner: `scripts/qualify_library_unit_relations.py`.
