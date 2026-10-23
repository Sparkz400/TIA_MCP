# Integrated preview contracts and decoder repair — 23 October 2026



A fresh full regression and portable package now cover the HMI preview,

component preview and unified effect contracts, together with the strict

component artifact decoder repair. These increments previously had separate

focused evidence. This run does not add native qualification.



## Combined results



The full `All` aggregate passes 1,907 Python tests, 45 managed suites, 25 Explorer

model/client checks and every configured Host workflow. The component suite

includes 263 checks. The package passes all 53 stages, including original and

relocated Host workflows and final integrity verification.



Additional validator checks pass 29 cases in each location across 23 modes,

using the packaged scripts and schemas from outside the repository with its

Python import path removed. Original input bytes stay unchanged. Both locations

also refuse the opaque synthetic HMI effect in strict combined mode. This

fixture is valid for its specialized HMI mode; the initial combined-mode

positive test was incorrectly configured. Its failed attempt and initial

checker are retained, and the corrected runs use separate `-02` directories.

No package or implementation change was needed for that correction.



Source snapshot: `artifacts/deployment-build/65769de042be4477ba703adb96f6739d`

(2,888 captured inputs). Bundle: `artifacts/releases/tia-mcp-offline_20261023_091553_2c839ec4` (2,141 files),

with adjacent ZIP/checksum. Manifest SHA-256: `38330b26352c38aea922c64c90c6206ba84ed23d1c1700ba3225f4de0929c481`.

The offline bundle excludes native workers. The separately recorded full V21

SDK build for the decoder repair passed without executing the worker.



## Evidence



The complete receipts are under `artifacts/todo-preview-integration-20261023`:



- `offline-01/result.json` and `offline-run.log`: full regression.

- `package-01/result.json` and `package-run.log`: full package verification.

- `observations-original-02/result.json` and `observations-relocated-02/result.json`:

  58 direct packaged validation checks, plus two expected refusals.

- `original-final-strict.json` and `relocated-final-strict.json`: strict bundle integrity.

- `snapshot-comparison.json`: every frozen source hash and complete file set,

  with subsequent documentation changes listed separately.

- `verification.json`: final hash-pinned aggregate receipt and prior increment receipts.



The catalog remains 335 features: 132 implemented, 193 partial and ten not

implemented. These labels are not a release completion percentage. The full

feasible 1.0 obligations, hosted CI and native/runtime/preservation acceptance

remain open; this batch did not install or publish a release.



## Separate next correction



While the frozen runs proceeded, an isolated draft under

`artifacts/todo-component-oracle-draft-20261023` reproduced ten cases where the

independent qualification preflight accepted honestly rehashed XML with wrong

roots, extra/hidden native objects, mismatched/duplicate/empty IDs, incorrect

FB composition or duplicate attribute scopes. The draft refuses all ten and

passes 117 component Python tests. Its source and regression test are outside

this package and aggregate and require integration before this follow-up is

closed. Original native fixture bytes and baseline failures are retained.



The subsequent combined draft under

`artifacts/todo-component-preservation-draft-20261023` also repairs twelve

existing-caller preservation failures: metadata/namespace/whitespace/declaration

changes, invalid network scope/identity and unpinned raw snapshot bytes. It

passes 119 component tests, including controlled insertions into 16 retained

native FC/FB templates. Four matching instance-DB files are explicitly excluded

from the code-caller template check. Eight selected historical attempts contain

no passed preservation claim; this is a scoped audit, not a complete historical

inventory. This combined draft is also outside the frozen package/aggregate.

