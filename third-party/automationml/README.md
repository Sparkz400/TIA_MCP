# AutomationML schema and fixture notices

The two CAEX schemas are embedded in `TiaMcp.Core` without modification.
Seven upstream AML examples are retained under
`tests/TiaMcp.AutomationMl.Tests/fixtures/upstream` as independent format fixtures.
They are not Siemens exports or native qualification evidence.

`sources.json` records exact upstream commit URLs, local paths, byte lengths and
SHA-256 digests. `License.txt` is the MIT notice from AutomationML/AML-UA-XSLT;
`automationml-python.LICENSE` is the MIT notice from AutomationML/automationml-python.
Package copying includes this directory, preserving attribution for the embedded
schemas. Tests verify all eleven vendored source/license artifacts byte for byte.
