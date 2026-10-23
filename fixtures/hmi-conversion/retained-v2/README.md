# Original v2 producer records

These are copied original bytes from the passing 20 October wave4 frozen
aggregate, before integer conversion was implemented. `fixture.json` records
the producer source manifest, original fixture path/hash/size, two selected
unaltered responses and every copied file's length/hash.

The successful mixed v1/v2 rename lineage and refused manual-current drift
retain 51 original dependency/input/record/candidate/source files (347,021
bytes). They are not regenerated lookalikes. Their original v1 authors and
all selected saved project reviews are included.

`scripts/test_hmi_integer_conversion_host.py` copies these files into its own
store, independently verifies their source and YAML semantics, reopens both
responses in two actual Host lifetimes, and rechecks all original bytes.
The fixture is synthetic engineering evidence; it establishes no native TIA
or runtime qualification.
