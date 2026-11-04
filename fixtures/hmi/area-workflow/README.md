# Area screen workflow fixture

`prototypes.xml` is synthetic, native-shaped offline input. It contains no customer
project export and is **not qualified for native import**. The managed workflow
test derives two blank screens and composes 66/56 controls using the same request
adapter as the worker. It exercises three languages, structured array references,
recipe/manual permission links, typed constants, navigation and optional equipment.
Preview content is inert even when captions contain HTML/script delimiters.

The tests also cover complete baseline errors, diagnostic regression, terminal
reconciliation, artifact isolation and explicit review findings. Native partial
import, reverse rollback and response-loss tests remain in
`scripts/qualify_hmi_classic_screen.py`; they require a separately configured,
owned Classic RT Advanced archive. Synthetic tests do not replace those tests or
establish runtime behavior, native rendering or a performance improvement.
