#nullable disable
using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    // Version one deliberately keeps LAD power flow distinct from FBD dataflow.
    // These plain data contracts have no Siemens or transport dependencies.
    internal sealed class TypedGraphicalModel
    {
        public int SchemaVersion { get; set; }
        public string Language { get; set; }
        public string Operation { get; set; }
        public string NetworkId { get; set; }
        public string InsertAfterNetworkId { get; set; }
        public TypedGraphicalNode[] Nodes { get; set; } = new TypedGraphicalNode[0];
        public TypedGraphicalConnection[] Connections { get; set; } = new TypedGraphicalConnection[0];
        public TypedGraphicalOperandRequest[] Operands { get; set; } = new TypedGraphicalOperandRequest[0];
        public TypedGraphicalCreation Creation { get; set; }
    }
    internal sealed class TypedGraphicalNode
    {
        public string Key { get; set; }
        public string Kind { get; set; }
        public string Operand { get; set; }
        public int InputCount { get; set; }
        public string Comparison { get; set; }
        public string DataType { get; set; }
        public string CalleePath { get; set; }
        public string Instance { get; set; }
        public string Instruction { get; set; }
        public string NativeUid { get; set; }
        public bool Negated { get; set; }
        public bool AllowSharedInstance { get; set; }
    }
    internal sealed class TypedGraphicalOperandRequest
    {
        public string Key { get; set; }
        public string Scope { get; set; }
        public string OwnerPath { get; set; }
        public string[] Components { get; set; } = new string[0];
        public string DataType { get; set; }
        public string Value { get; set; }
    }
    internal sealed class TypedGraphicalEndpoint
    {
        public string Node { get; set; }
        public string Port { get; set; }
    }
    internal sealed class TypedGraphicalConnection
    {
        public TypedGraphicalEndpoint From { get; set; }
        public TypedGraphicalEndpoint To { get; set; }
    }
    internal sealed class TypedGraphicalCreation
    {
        public string Name { get; set; }
        public string BlockType { get; set; }
        public TypedGraphicalDeclaration[] Interface { get; set; } = new TypedGraphicalDeclaration[0];
    }
    internal sealed class TypedGraphicalDeclaration
    {
        public string Name { get; set; }
        public string Section { get; set; }
        public string DataType { get; set; }
    }
    internal sealed class TypedGraphicalOperand
    {
        public string Key { get; set; }
        public string Identity { get; set; }
        public string Scope { get; set; }
        public string[] Components { get; set; } = new string[0];
        public string DataType { get; set; }
        public string Value { get; set; }
        public bool Writable { get; set; }
        public string DeclarationHash { get; set; }
    }
    internal sealed class TypedGraphicalCallee
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public string BlockType { get; set; }
        public string Namespace { get; set; }
        public string InterfaceHash { get; set; }
        public TypedGraphicalParameter[] Parameters { get; set; } = new TypedGraphicalParameter[0];
        // Software scope that owns the callee; a callee outside TargetScopePath is a cross-unit call.
        public string ScopePath { get; set; }
        // Native UnitAccessType of a cross-unit callee ("Published"/"Unpublished"); null in the caller's own scope.
        public string UnitAccess { get; set; }
    }
    internal sealed class TypedGraphicalParameter
    {
        public string Name { get; set; }
        public string Section { get; set; }
        public string DataType { get; set; }
        public bool Required { get; set; } = true;
    }
    internal sealed class TypedGraphicalInstance
    {
        public string Identity { get; set; }
        public string Scope { get; set; }
        public string[] Components { get; set; } = new string[0];
        public string CalleePath { get; set; }
        public string DataType { get; set; }
        public string DeclarationHash { get; set; }
    }
    internal sealed class TypedGraphicalContext
    {
        public Dictionary<string, TypedGraphicalOperand> Operands { get; set; } = new Dictionary<string, TypedGraphicalOperand>(StringComparer.Ordinal);
        public Dictionary<string, TypedGraphicalCallee> Callees { get; set; } = new Dictionary<string, TypedGraphicalCallee>(StringComparer.Ordinal);
        public Dictionary<string, TypedGraphicalInstance> Instances { get; set; } = new Dictionary<string, TypedGraphicalInstance>(StringComparer.Ordinal);
        public string TiaVersion { get; set; } = "V21";
        public string CpuFamily { get; set; }
        public string Route { get; set; } = "standard";
        public string TargetNamespace { get; set; }
        public string TargetScopePath { get; set; }
        // Units the caller's unit declares a direct SoftwareUnit relation to, as exact scope paths.
        public string[] RelatedScopePaths { get; set; } = new string[0];
        public string NetworkTemplateXml { get; set; }
        // An independently observed, validated native template is required for advanced profiles.
        // The resolver loads these from the deployed profile catalog, never the user payload.
        public TypedGraphicalProfile[] Profiles { get; set; } = new TypedGraphicalProfile[0];
    }
    internal sealed class TypedGraphicalProfile
    {
        public string Id { get; set; }
        public int Version { get; set; } = 1;
        public string Language { get; set; }
        public string Kind { get; set; }
        public string Instruction { get; set; }
        public string DataType { get; set; }
        public string TiaVersion { get; set; }
        public string CpuFamily { get; set; }
        public string Route { get; set; }
        public string NativeName { get; set; }
        public string NativeTemplateXml { get; set; }
        public string CalleeNameEncoding { get; set; }
        public string NetworkTemplateXml { get; set; }
        public string SourceSha256 { get; set; }
        public string Provenance { get; set; }
        public bool NativeFormatValidated { get; set; }
        public bool ReaderOnly { get; set; }
        public bool EngineeringVerified { get; set; }
        public bool PreservationVerified { get; set; }
        public bool PersistenceVerified { get; set; }
        public bool RecoveryVerified { get; set; }
        public bool RuntimeVerified { get; set; }
        public string InstanceType { get; set; }
        public string InstanceDeclarationXml { get; set; }
        public string GlobalInstanceType { get; set; }
        public string GlobalInstanceTemplateXml { get; set; }
        public TypedGraphicalPin[] Pins { get; set; } = new TypedGraphicalPin[0];
    }
    internal sealed class TypedGraphicalPin
    {
        public string Name { get; set; }
        public string Direction { get; set; }
        public string DataType { get; set; }
        public bool Required { get; set; } = true;
    }
    internal sealed class TypedGraphicalBuildResult
    {
        public XDocument Document { get; set; }
        public string NetworkId { get; set; }
        public Dictionary<string, string> IdMapping { get; set; }
        public string[] Profiles { get; set; }
        public TypedGraphicalEffect[] Effects { get; set; }
        public TypedGraphicalDiagnostic[] Diagnostics { get; set; }
        public string SourceSha256 { get; set; }
        public string ResultSha256 { get; set; }
    }
    internal sealed class TypedGraphicalEffect
    {
        public string Kind { get; set; }
        public string Identity { get; set; }
        public string SourceSha256 { get; set; }
        public string Description { get; set; }
    }
    internal sealed class TypedGraphicalDiagnostic
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public string Node { get; set; }
        public string Severity { get; set; }
    }
    internal sealed class TypedGraphicalReadResult
    {
        public int SchemaVersion { get; set; } = 1;
        public string BlockPath { get; set; }
        public string SourceSha256 { get; set; }
        public string NativeXml { get; set; }
        public TypedGraphicalReadNetwork[] Networks { get; set; }
    }
    internal sealed class TypedGraphicalReadNetwork
    {
        public string NetworkId { get; set; }
        public string Language { get; set; }
        public string Semantics { get; set; }
        public string Coverage { get; set; }
        public string NativeXml { get; set; }
        public string SourceSha256 { get; set; }
        public TypedGraphicalReadNode[] Nodes { get; set; }
        public TypedGraphicalReadConnection[] Connections { get; set; }
        public TypedGraphicalDiagnostic[] Diagnostics { get; set; }
    }
    internal sealed class TypedGraphicalReadNode
    {
        public string Key { get; set; }
        public string NativeUid { get; set; }
        public string Kind { get; set; }
        public string NativeName { get; set; }
        public bool Supported { get; set; }
        public string NativeXml { get; set; }
        public string SourceSha256 { get; set; }
        public string ProfileId { get; set; }
        public TypedGraphicalPin[] Pins { get; set; } = new TypedGraphicalPin[0];
        public string[] NegatedPins { get; set; } = new string[0];
    }
    internal sealed class TypedGraphicalReadConnection
    {
        public string NativeUid { get; set; }
        public TypedGraphicalEndpoint[] Endpoints { get; set; }
        public bool Resolved { get; set; }
        public string NativeXml { get; set; }
    }
}
