# Public API metadata inspector

`../Inspect-PublicApi.ps1` inventories public DLLs through `MetadataLoadContext`. It does not execution-load Siemens DLLs, instantiate attributes, invoke members, construct Siemens objects or connect to a native target. Windows PowerShell 7/.NET and an installed .NET SDK containing `System.Reflection.MetadataLoadContext.dll` are required. The default SDK inventory is V21/net48; framework resolution uses the installed .NET Framework 4 reference/runtime metadata.

```powershell
./scripts/Inspect-PublicApi.ps1 -ReportPath artifacts/new-run/public-api-metadata.json
./scripts/public_api_metadata/Test-PublicApiMetadata.ps1 -OutputDirectory artifacts/new-run/metadata-fixture
```

Both destinations must be new. The test compiles an inert synthetic .NET Framework fixture with the installed framework C# compiler, then inspects metadata. It compares compiler-produced XML IDs and independently authored IDs, generic/inherited identities, read/write accessor visibility, integer precision, attributes, constraints, defaults, immutable publication and bounds. The fixture is never executed.

The JSON has `schemaVersion:1`, `inspectionMode:metadataOnly`, `nativeCodeExecuted:false`, `targetProbed:false` and `memberScope:declaredPublic`. Assembly records carry exact path/version/SHA-256 and associated XML hashes. Types contain metadata kind/base/interfaces/generic constraints/enum values/attributes, declared `members`, and `inheritedMemberIds`. Type definitions use CLR FullName; XML IDs replace nested `+` with `.`. Member IDs use the declaring definition, including inherited generic declarations, while type base/interface expressions retain concrete generic arguments. Constants are invariant strings to avoid losing 64-bit precision in JSON consumers.

Each member records exact XML ID/status, declaring assembly/type, signature, parameters, return type, public read/write accessors, generic constraints and custom-attribute data. Property/event accessor methods are marked `accessor:true`. `xmlDocumentationPresent` is checked against the containing inventory assembly's XML file; inherited IDs may refer to a different assembly and are not duplicated as declared rows. Missing XML documentation does not imply a missing or unsupported API. Private explicit-interface implementation IDs found only in XML are outside the declared-public inventory and must remain coverage residuals where semantically relevant.

Limits fail before publishing rather than quietly truncate. A type/member reflection failure is retained explicitly; unresolved encodings do not receive invented IDs. Metadata load failure aborts the run. Runtime self-description attributes/services and native target applicability are not derivable from DLL metadata. Assembly/XML/source hashes are checked across inspection; a changed helper already loaded in the PowerShell process is rejected. The final UTF-8 report is bounded to 128 MiB and created without overwriting an existing report.
