# Independent HMI scalar comparisons — 23 October 2026

The old qualifier could reuse a single matching observation for multiple native
rows with the same type and name. A second observation with changed geometry
could therefore pass. It also accepted an absent property as a native null and
Python Boolean/number aliases as matching native values.

The comparison now requires a one-to-one assignment. Iterative augmenting paths
handle overlapping known attributes without greedy order dependence or recursive
stack growth. Required native scalar properties must be present; Boolean values
remain distinct from numbers. Unknown getter values are still outside this
scalar-only comparison; native selectors and full HMI content are not qualified.

Four false acceptances are reproduced against the original committed qualifier
and refused by the repair. All 50 HMI qualification tests pass, including 36
permutations of an ambiguous matching case. All 24 configured-fixture tests pass.
The unchanged root-g and root-i native reports from 8 October retain all 84
section outcomes: 78 passed and six Classic alarm observations unsupported.
Their original source hashes match. Screen-item comparison is a separate path
and was not changed or counted in those 84 section checks.

Evidence: `artifacts/hmi-independent-comparison-20261023/false-acceptances.json`,
`retained-comparisons.json`, `configured-tests.log`, and
`artifacts/native-resume-20261023/hmi-comparison-tests-01.log`.
The initial compatibility script attempted to look up a profile without any
observations; the corrected script skips those before native-baseline lookup.
Neither original native report was edited. No broad acceptance gate is promoted.
The earlier full aggregate/package predates this Python qualifier repair; it has
not been rebuilt for this focused change.
