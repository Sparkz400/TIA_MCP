param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$outputFull=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputFull) { throw 'Choose a new immutable fixture output directory.' }
[IO.Directory]::CreateDirectory($outputFull) | Out-Null
$source=@'
using System;
using System.Collections.Generic;
namespace MetadataFixture {
    /// <summary>Enum oracle.</summary>
    public enum Mode : ulong {
        /// <summary>Preserve integer precision.</summary>
        Maximum = ulong.MaxValue
    }
    /// <summary>Generic base oracle.</summary>
    public class Parent<T> {
        /// <summary>Inherited generic declaration.</summary>
        public virtual T Echo(T value) { throw new Exception("Must never execute"); }
    }
    /// <summary>Derived oracle.</summary>
    public class Child : Parent<string> {
        static Child() { throw new Exception("Fixture code must never execute"); }
        /// <summary>Constructor parameters.</summary>
        public Child(int x) { throw new Exception("Must never execute"); }
        /// <summary>Accessor visibility.</summary>
        public int Value { get { throw new Exception(); } private set { throw new Exception(); } }
        /// <summary>Indexer.</summary>
        public string this[int i, string text] { get { throw new Exception(); } set { throw new Exception(); } }
        /// <summary>Generic method and constraints.</summary>
        public U Convert<U>(Dictionary<string,Tup<U>>[] input, ref int value, out string result) where U : class, IDisposable, new() { throw new Exception(); }
        /// <summary>Multi-dimensional and jagged arrays.</summary>
        public void Arrays(int[,] matrix, int[][] jagged) { throw new Exception(); }
        /// <summary>Optional literal.</summary>
        public void Optional(int value = 7) { throw new Exception(); }
        /// <summary>Conversion suffix.</summary>
        public static explicit operator int(Child value) { throw new Exception(); }
        /// <summary>Event metadata.</summary>
        public event EventHandler Changed { add { throw new Exception(); } remove { throw new Exception(); } }
        /// <summary>Custom attribute metadata, without instantiation.</summary>
        [Obsolete("metadata fixture", false)]
        public const long Limit = long.MaxValue;
    }
    /// <summary>Generic parameter helper.</summary>
    public class Tup<T> {}
    /// <summary>Nested generics.</summary>
    public class Outer<T> {
        /// <summary>Nested declaration.</summary>
        public class Inner<U> {
            /// <summary>Nested parameter ownership.</summary>
            public void Pair(Outer<T>.Inner<U> pair) { throw new Exception(); }
        }
    }
}
'@
$sourcePath=Join-Path $outputFull 'Fixture.cs'
[IO.File]::WriteAllText($sourcePath,$source,[Text.UTF8Encoding]::new($false))
$assemblyPath=Join-Path $outputFull 'Fixture.dll'
$xmlPath=Join-Path $outputFull 'Fixture.xml'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$compilerOutput=& $compiler '/nologo' '/target:library' "/out:$assemblyPath" "/doc:$xmlPath" $sourcePath 2>&1
if ($LASTEXITCODE -ne 0) { throw ($compilerOutput -join [Environment]::NewLine) }
$reportPath=Join-Path $outputFull 'metadata.json'
& (Join-Path $PSScriptRoot '../Inspect-PublicApi.ps1') -ApiDirectory $outputFull -ReportPath $reportPath | Out-Null
$report=Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$types=@($report.assemblies[0].types)
$members=@($types | ForEach-Object members)
$ids=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($type in $types) { [void]$ids.Add($type.id) }
foreach ($member in $members) { [void]$ids.Add($member.id) }
[xml]$documentation=Get-Content -LiteralPath $xmlPath -Raw
$missing=@($documentation.doc.members.member | Where-Object {-not $ids.Contains($_.name)} | ForEach-Object name)
if ($missing.Count) { throw ('Compiler documentation IDs absent: '+($missing -join ', ')) }
function Assert-True($Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
$expected=@(
 'M:MetadataFixture.Child.#ctor(System.Int32)',
 'P:MetadataFixture.Child.Item(System.Int32,System.String)',
 'M:MetadataFixture.Child.Convert``1(System.Collections.Generic.Dictionary{System.String,MetadataFixture.Tup{``0}}[],System.Int32@,System.String@)',
 'M:MetadataFixture.Child.Arrays(System.Int32[0:,0:],System.Int32[][])',
 'M:MetadataFixture.Child.op_Explicit(MetadataFixture.Child)~System.Int32',
 'M:MetadataFixture.Outer`1.Inner`1.Pair(MetadataFixture.Outer{`0}.Inner{`1})'
)
foreach ($id in $expected) { Assert-True ($ids.Contains($id)) "Independent expected XML ID missing: $id" }
$child=$types | Where-Object name -eq 'MetadataFixture.Child'
Assert-True ($child.inheritedMemberIds -contains 'M:MetadataFixture.Parent`1.Echo(`0)') 'Inherited generic method lost original declaration ID.'
Assert-True ($child.baseType -eq 'MetadataFixture.Parent{System.String}') 'Constructed generic base mapping not retained.'
Assert-True (-not ($child.members | Where-Object name -eq 'Echo')) 'Inherited member repeated as a declared member.'
$value=$child.members | Where-Object {$_.kind -eq 'Property' -and $_.name -eq 'Value'}
Assert-True ($value.read -and -not $value.write) 'Private property setter promoted to public write.'
$convert=$child.members | Where-Object name -eq 'Convert'
Assert-True ($convert.genericParameters[0].constraints -contains 'System.IDisposable') 'Generic constraint missing.'
$optional=$child.members | Where-Object name -eq 'Optional'
Assert-True ($optional.parameters[0].isOptional -and $optional.parameters[0].defaultValue -eq '7') 'Optional default lost.'
$mode=$types | Where-Object name -eq 'MetadataFixture.Mode'
Assert-True ($mode.enumValues[0].value -eq '18446744073709551615') 'Enum constant lost unsigned precision.'
$constant=$child.members | Where-Object name -eq 'Limit'
Assert-True ($constant.constant -eq '9223372036854775807' -and -not $constant.write) 'Literal constant precision or mutability wrong.'
Assert-True ($constant.attributes.type -contains 'System.ObsoleteAttribute') 'Custom attribute metadata lost.'
Assert-True (-not ($members | Where-Object idStatus -ne 'exact')) 'Unresolved fixture IDs.'
Assert-True (-not $report.nativeCodeExecuted -and -not $report.targetProbed) 'Metadata-only flags missing.'
$before=(Get-FileHash -LiteralPath $reportPath).Hash
$overwriteRejected=$false
try { & (Join-Path $PSScriptRoot '../Inspect-PublicApi.ps1') -ApiDirectory $outputFull -ReportPath $reportPath | Out-Null }
catch { $overwriteRejected=$_.Exception.Message -match 'already exists' }
Assert-True ($overwriteRejected -and (Get-FileHash -LiteralPath $reportPath).Hash -eq $before) 'Immutable output guard failed.'
$boundRejected=$false
try { & (Join-Path $PSScriptRoot '../Inspect-PublicApi.ps1') -ApiDirectory $outputFull -ReportPath (Join-Path $outputFull 'must-not-exist.json') -MaximumTypes 1 | Out-Null }
catch { $boundRejected=$_.Exception.Message -match 'bound exceeded' }
Assert-True ($boundRejected -and -not (Test-Path (Join-Path $outputFull 'must-not-exist.json'))) 'Bound failure published partial inventory.'
$result=[ordered]@{passed=$true; compilerOracleIds=@($documentation.doc.members.member).Count; independentlyExpectedIds=$expected.Count;
  checked=@('constructors','indexers','generic method','constraints','byref/out','multi-dimensional arrays','nested generics','inherited generic identity','public accessor visibility','optional default','enum precision','literal precision','custom attributes','metadata-only flags','immutable output','bounded failure');
  reportPath=$reportPath;reportSha256=$before.ToLowerInvariant();nativeCodeExecuted=$false;targetProbed=$false}
$receipt=Join-Path $outputFull 'result.json'
[IO.File]::WriteAllText($receipt,($result|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
$result|ConvertTo-Json -Depth 5
