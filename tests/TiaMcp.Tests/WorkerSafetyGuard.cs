using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace TiaMcp.Tests;

/// <summary>
/// Reads PE/CLI metadata only. Never loads the worker or any Siemens assembly.
/// TiaPortalProcess.Dispose terminates the represented TIA process, so
/// discovery/attachment descriptors stay inside a disposal-free boundary.
/// The separate qualification owner can close only a Portal it constructs itself.
/// </summary>
internal static class WorkerSafetyGuard
{
    private const string Boundary = "TiaMcp.Openness.V21.PortalProcesses";
    private const string ProcessType = "Siemens.Engineering.TiaPortalProcess";
    private const string OwnedBoundary = "TiaMcp.Openness.V21.OwnedQualificationPortal";
    private const string PortalType = "Siemens.Engineering.TiaPortal";
    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    internal static void Verify(string path) => Verify(path, Boundary, ProcessType, inspectOutsideBoundary: true, OwnedBoundary, PortalType);

    private static void Verify(string path, string boundary, string processType, bool inspectOutsideBoundary, string? ownedBoundary = null, string? portalType = null)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var names = new TypeNames(metadata);
        var boundaryFound = false;
        var ownedFound = ownedBoundary is null;
        var violations = new List<string>();
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var owner = names.Name(typeHandle);
            var inside = owner == boundary || owner.StartsWith(boundary + "+", StringComparison.Ordinal);
            var owned = owner == ownedBoundary;
            if (owner == boundary) boundaryFound = true;
            if (!inside && !owned && !inspectOutsideBoundary) continue;
            var type = metadata.GetTypeDefinition(typeHandle);
            if (owned)
            {
                ownedFound = true;
                VerifyOwnedCreator(pe, metadata, names, type, owner, portalType!, processType, violations);
            }
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                var methodName = owner + "." + metadata.GetString(method.Name);
                var signature = method.DecodeSignature(names, null);
                if (Mentions(signature, processType))
                    violations.Add(methodName + " exposes a raw process descriptor in its signature.");
                if (method.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
                foreach (var instruction in TokenInstructions(il))
                {
                    var target = names.Describe(instruction.Handle);
                    var disposal = target.MethodName is "Dispose" or "DisposeAsync";
                    if (inside && disposal)
                        violations.Add($"{methodName} IL_{instruction.Offset:x4} invokes disposal: {target.Description}.");
                    else if (!inside && !owned && target.Description.Contains(processType, StringComparison.Ordinal))
                        violations.Add($"{methodName} IL_{instruction.Offset:x4} accesses a raw process descriptor outside {boundary}: {target.Description}.");
                }
            }
            foreach (var fieldHandle in type.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                if (field.DecodeSignature(names, null).Contains(processType, StringComparison.Ordinal))
                    violations.Add(owner + "." + metadata.GetString(field.Name) + " retains a raw process descriptor in a field.");
            }
        }
        if (!boundaryFound) violations.Add("Required descriptor boundary is missing: " + boundary + ".");
        if (!ownedFound) violations.Add("Required owned qualification boundary is missing: " + ownedBoundary + ".");
        if (violations.Count > 0)
            throw new InvalidOperationException("Unsafe worker process lifetime:\n" + string.Join("\n", violations));
    }

    // This exception is structural: no injected Portal/PID, one readonly Portal
    // created by a parameterless constructor, no attachment/discovery/helpers,
    // and raw process access only while closing that retained owned connection.
    private static void VerifyOwnedCreator(PEReader pe, MetadataReader metadata, TypeNames names,
        TypeDefinition type, string owner, string portalType, string processType, List<string> violations)
    {
        int constructors = 0, creations = 0, processCloses = 0, portalFields = 0;
        if ((type.Attributes & TypeAttributes.Sealed) == 0) violations.Add(owner + " must be sealed.");
        foreach (var handle in type.GetFields())
        {
            var field = metadata.GetFieldDefinition(handle);
            var fieldType = field.DecodeSignature(names, null);
            if (fieldType == portalType)
            {
                portalFields++;
                if ((field.Attributes & FieldAttributes.InitOnly) == 0 || (field.Attributes & FieldAttributes.Static) != 0)
                    violations.Add(owner + " must retain one readonly instance Portal.");
            }
            else if (fieldType != "Boolean") violations.Add(owner + " has an unexpected ownership field.");
        }
        foreach (var handle in type.GetMethods())
        {
            var method = metadata.GetMethodDefinition(handle);
            var name = metadata.GetString(method.Name);
            var signature = method.DecodeSignature(names, null);
            if (name is not (".ctor" or "get_Connection" or "Dispose") || signature.ParameterTypes.Length != 0 ||
                (method.Attributes & MethodAttributes.Static) != 0)
                violations.Add(owner + "." + name + " permits an unreviewed ownership entry point.");
            if (name == ".ctor") constructors++;
            if (method.RelativeVirtualAddress == 0) { violations.Add(owner + " has no auditable body."); continue; }
            foreach (var instruction in TokenInstructions(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? []))
            {
                var target = names.Describe(instruction.Handle);
                if (target.MethodName is null) continue;
                if (target.Description.StartsWith(portalType + "..ctor(", StringComparison.Ordinal) && name == ".ctor" && instruction.Opcode == OpCodes.Newobj)
                    creations++;
                else if (target.Description == "System.Object..ctor():Void" && name == ".ctor") { }
                else if (target.Description == portalType + ".GetCurrentProcess():" + processType && name == "Dispose") { }
                else if (target.Description == portalType + ".Dispose():Void" && name == "Dispose") { }
                else if (target.Description == processType + ".Dispose():Void" && name == "Dispose") processCloses++;
                else violations.Add(owner + "." + name + " invokes an unreviewed ownership dependency: " + target.Description);
            }
        }
        if (constructors != 1 || creations != 1 || processCloses != 1 || portalFields != 1)
            violations.Add(owner + " must create and close exactly its own retained Portal.");
    }

    private static bool Mentions(MethodSignature<string> signature, string type) =>
        signature.ReturnType.Contains(type, StringComparison.Ordinal) ||
        signature.ParameterTypes.Any(parameter => parameter.Contains(type, StringComparison.Ordinal));

    private static IEnumerable<(int Offset, EntityHandle Handle, OpCode Opcode)> TokenInstructions(byte[] il)
    {
        var offset = 0;
        while (offset < il.Length)
        {
            var start = offset;
            short value = il[offset++];
            if (value == 0xfe)
            {
                if (offset >= il.Length) throw new BadImageFormatException("Truncated IL opcode.");
                value = unchecked((short)(0xfe00 | il[offset++]));
            }
            if (!OpCodesByValue.TryGetValue(value, out var opcode)) throw new BadImageFormatException("Unknown IL opcode.");
            var size = opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineI or OperandType.ShortInlineBrTarget or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => SwitchBytes(il, offset),
                _ => 4
            };
            if (size < 0 || offset + size > il.Length) throw new BadImageFormatException("Truncated IL operand.");
            if (opcode.OperandType is OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok or OperandType.InlineField)
                yield return (start, MetadataTokens.EntityHandle(BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4))), opcode);
            offset += size;
        }
    }

    private static int SwitchBytes(byte[] il, int offset)
    {
        if (offset + 4 > il.Length) throw new BadImageFormatException("Truncated switch operand.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
        if (count < 0) throw new BadImageFormatException("Invalid switch operand.");
        return checked(4 + count * 4);
    }

    internal static Task TestsAcceptSafe()
    {
        VerifyFixture(typeof(SafetyFixtures.Safe));
        VerifyOwnedFixture(typeof(SafetyFixtures.OwnedPortal));
        return Task.CompletedTask;
    }

    internal static async Task TestsRejectUnsafe()
    {
        foreach (var type in new[] { typeof(SafetyFixtures.ExplicitDispose), typeof(SafetyFixtures.UsingDispose), typeof(SafetyFixtures.NestedDispose), typeof(SafetyFixtures.EscapingDescriptor) })
            await Check.ThrowsAsync<InvalidOperationException>(() =>
            {
                VerifyFixture(type);
                return Task.CompletedTask;
            });
        foreach (var type in new[] { typeof(SafetyFixtures.InjectedPortal), typeof(SafetyFixtures.AttachedPortal), typeof(SafetyFixtures.MutablePortal) })
            await Check.ThrowsAsync<InvalidOperationException>(() =>
            {
                VerifyOwnedFixture(type);
                return Task.CompletedTask;
            });
    }

    private static void VerifyOwnedFixture(Type owner) => Verify(Assembly.GetExecutingAssembly().Location,
        typeof(SafetyFixtures.Safe).FullName!, typeof(SafetyFixtures.ProcessDescriptor).FullName!, false,
        owner.FullName!, typeof(SafetyFixtures.Portal).FullName!);

    private static void VerifyFixture(Type boundary) => Verify(Assembly.GetExecutingAssembly().Location,
        boundary.FullName!, typeof(SafetyFixtures.ProcessDescriptor).FullName!, inspectOutsideBoundary: false);

    private sealed class TypeNames(MetadataReader reader) : ISignatureTypeProvider<string, object?>
    {
        internal string Name(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => Definition((TypeDefinitionHandle)handle),
            HandleKind.TypeReference => Reference((TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null),
            _ => "<" + handle.Kind + ">"
        };

        private string Definition(TypeDefinitionHandle handle)
        {
            var definition = reader.GetTypeDefinition(handle);
            var parent = definition.GetDeclaringType();
            return parent.IsNil ? Join(reader.GetString(definition.Namespace), reader.GetString(definition.Name))
                : Name(parent) + "+" + reader.GetString(definition.Name);
        }

        private string Reference(TypeReferenceHandle handle)
        {
            var reference = reader.GetTypeReference(handle);
            return reference.ResolutionScope.Kind == HandleKind.TypeReference
                ? Name(reference.ResolutionScope) + "+" + reader.GetString(reference.Name)
                : Join(reader.GetString(reference.Namespace), reader.GetString(reference.Name));
        }

        internal (string Description, string? MethodName) Describe(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.MemberReference:
                    var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                    var memberName = reader.GetString(member.Name);
                    if (member.GetKind() == MemberReferenceKind.Field)
                        return (Name(member.Parent) + "." + memberName + ":" + member.DecodeFieldSignature(this, null), null);
                    return (DescribeMethod(Name(member.Parent), memberName, member.DecodeMethodSignature(this, null)), memberName);
                case HandleKind.MethodDefinition:
                    var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                    var methodName = reader.GetString(method.Name);
                    return (DescribeMethod(Name(method.GetDeclaringType()), methodName, method.DecodeSignature(this, null)), methodName);
                case HandleKind.MethodSpecification:
                    return Describe(reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
                default:
                    return (Name(handle), null);
            }
        }

        private static string DescribeMethod(string owner, string name, MethodSignature<string> signature) =>
            owner + "." + name + "(" + string.Join(",", signature.ParameterTypes) + "):" + signature.ReturnType;
        private static string Join(string space, string name) => space.Length == 0 ? name : space + "." + name;
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', Math.Max(shape.Rank - 1, 0)) + "]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => DescribeMethod("", "function", signature);
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind) => Name(handle);
        public string GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind) => Name(handle);
        public string GetTypeFromSpecification(MetadataReader metadata, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => Name(handle);
    }
}

// These synthetic classes are only read as IL by the guard's self-tests. They are never invoked.
internal static class SafetyFixtures
{
    internal sealed class Portal : IDisposable
    {
        public Portal() { }
        public static Portal Attach() => throw new InvalidOperationException("Synthetic attachment must never run.");
        public ProcessDescriptor GetCurrentProcess() => throw new InvalidOperationException("Metadata only.");
        public void Dispose() => throw new InvalidOperationException("Metadata only.");
    }

    internal sealed class OwnedPortal : IDisposable
    {
        private readonly Portal _connection;
        public OwnedPortal() { _connection = new Portal(); }
        public Portal Connection => _connection;
        public void Dispose() { var process = _connection.GetCurrentProcess(); _connection.Dispose(); process.Dispose(); }
    }

    internal sealed class InjectedPortal : IDisposable
    {
        private readonly Portal _connection;
        public InjectedPortal(Portal connection) { _connection = connection; }
        public void Dispose() { var process = _connection.GetCurrentProcess(); _connection.Dispose(); process.Dispose(); }
    }

    internal sealed class AttachedPortal : IDisposable
    {
        private readonly Portal _connection;
        public AttachedPortal() { _connection = Portal.Attach(); }
        public void Dispose() { var process = _connection.GetCurrentProcess(); _connection.Dispose(); process.Dispose(); }
    }

    internal sealed class MutablePortal : IDisposable
    {
        private Portal _connection;
        public MutablePortal() { _connection = new Portal(); }
        public void Dispose() { var process = _connection.GetCurrentProcess(); _connection.Dispose(); process.Dispose(); }
    }

    internal sealed class ProcessDescriptor : IDisposable
    {
        public int Id => 123;
        public void Dispose() => throw new InvalidOperationException("Synthetic destructive operation must never run.");
    }

    internal static class Safe
    {
        public static int Inspect() => new ProcessDescriptor().Id;
    }

    internal static class ExplicitDispose
    {
        public static void Inspect() => new ProcessDescriptor().Dispose();
    }

    internal static class UsingDispose
    {
        public static int Inspect()
        {
            using var process = new ProcessDescriptor();
            return process.Id;
        }
    }

    internal static class NestedDispose
    {
        private static class Helper
        {
            public static void Inspect() => ((IDisposable)new ProcessDescriptor()).Dispose();
        }
        public static void Inspect() => Helper.Inspect();
    }

    internal static class EscapingDescriptor
    {
        public static ProcessDescriptor Inspect() => new();
    }
}
