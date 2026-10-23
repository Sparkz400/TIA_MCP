using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace TiaMcp.PublicApiMetadata
{
    // Only reflection metadata is consumed. No GetValue, attribute instantiation,
    // assembly execution loading, native call, Siemens constructor or service call.
    public sealed class Result
    {
        public List<TypeRecord> Types { get; set; } = new List<TypeRecord>();
        public List<string> Errors { get; set; } = new List<string>();
    }
    public sealed class TypeRecord
    {
        public string name { get; set; }
        public string id { get; set; }
        public string baseType { get; set; }
        public string kind { get; set; }
        public bool isAbstract { get; set; }
        public bool isSealed { get; set; }
        public bool isNested { get; set; }
        public string[] interfaces { get; set; }
        public string[] inheritedMemberIds { get; set; }
        public object[] genericParameters { get; set; }
        public object[] enumValues { get; set; }
        public object[] attributes { get; set; }
        public List<Dictionary<string, object>> members { get; set; } = new List<Dictionary<string, object>>();
        public List<string> errors { get; set; } = new List<string>();
    }
    public static class Inspector
    {
        const BindingFlags PublicMembers = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        public static Result Inspect(Assembly assembly, int maxTypes, int maxMembers)
        {
            var result = new Result();
            Type[] types;
            try { types = assembly.GetExportedTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null && t.IsVisible).ToArray();
                result.Errors.AddRange(ex.LoaderExceptions.Select(e => e.GetType().FullName + ": " + e.Message));
            }
            if (types.Length > maxTypes) throw new InvalidOperationException("Type inventory bound exceeded.");
            int memberCount = 0;
            foreach (Type type in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                var row = new TypeRecord { name = type.FullName, id = "T:" + DefinitionName(type),
                    kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class",
                    isAbstract = type.IsAbstract, isSealed = type.IsSealed, isNested = type.IsNested };
                result.Types.Add(row);
                try
                {
                    row.baseType = type.BaseType == null ? null : TypeReference(type.BaseType);
                    row.interfaces = type.GetInterfaces().Select(TypeReference).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    row.genericParameters = GenericParameters(type.IsGenericTypeDefinition ? type.GetGenericArguments() : Type.EmptyTypes);
                    row.attributes = Attributes(type.GetCustomAttributesData());
                    row.enumValues = type.IsEnum ? type.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(f => f.Name, StringComparer.Ordinal)
                        .Select(f => (object)new Dictionary<string, object> { ["name"] = f.Name, ["value"] = Constant(f.GetRawConstantValue()), ["type"] = f.GetRawConstantValue()?.GetType().FullName }).ToArray() : new object[0];
                    row.inheritedMemberIds = type.GetMembers(PublicMembers).Where(m => m.DeclaringType != type && (m is MethodBase || m is PropertyInfo || m is FieldInfo || m is EventInfo))
                        .Select(XmlId).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    foreach (MemberInfo member in type.GetMembers(PublicMembers | BindingFlags.DeclaredOnly).Where(m => m is MethodBase || m is PropertyInfo || m is FieldInfo || m is EventInfo)
                        .OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.ToString(), StringComparer.Ordinal))
                    {
                        if (++memberCount > maxMembers) throw new InvalidOperationException("Member inventory bound exceeded.");
                        row.members.Add(Member(member, type));
                    }
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("inventory bound")) { throw; }
                catch (Exception ex) { row.errors.Add(ex.GetType().FullName + ": " + ex.Message); }
            }
            return result;
        }

        static Dictionary<string, object> Member(MemberInfo member, Type context)
        {
            var result = new Dictionary<string, object> { ["id"] = null, ["kind"] = member.MemberType.ToString(), ["name"] = member.Name,
                ["declaringType"] = member.DeclaringType?.FullName, ["contextType"] = context.FullName,
                ["signature"] = member.ToString(), ["read"] = null, ["write"] = null,
                ["genericParameters"] = new object[0], ["parameters"] = new object[0], ["returnType"] = null,
                ["attributes"] = new object[0], ["accessor"] = false, ["isInherited"] = member.DeclaringType != context,
                ["idStatus"] = "unresolved", ["error"] = null };
            try
            {
                MemberInfo definition = DefinitionMember(member);
                result["id"] = XmlId(definition);
                result["idStatus"] = "exact";
                result["declaringType"] = DefinitionName(definition.DeclaringType);
                result["declaringAssembly"] = definition.DeclaringType.Assembly.GetName().Name;
                result["attributes"] = Attributes(member.GetCustomAttributesData());
                if (member is MethodBase method)
                {
                    result["parameters"] = Parameters(method.GetParameters());
                    result["genericParameters"] = GenericParameters(method.IsGenericMethodDefinition ? method.GetGenericArguments() : Type.EmptyTypes);
                    result["accessor"] = method.IsSpecialName && (method.Name.StartsWith("get_", StringComparison.Ordinal) || method.Name.StartsWith("set_", StringComparison.Ordinal) || method.Name.StartsWith("add_", StringComparison.Ordinal) || method.Name.StartsWith("remove_", StringComparison.Ordinal) || method.Name.StartsWith("raise_", StringComparison.Ordinal));
                    result["isStatic"] = method.IsStatic;
                    result["isAbstract"] = method.IsAbstract;
                    result["isVirtual"] = method.IsVirtual;
                    if (method is MethodInfo mi) result["returnType"] = TypeReference(mi.ReturnType);
                }
                if (member is PropertyInfo property)
                {
                    result["read"] = property.GetGetMethod() != null;
                    result["write"] = property.GetSetMethod() != null;
                    result["parameters"] = Parameters(property.GetIndexParameters());
                    result["returnType"] = TypeReference(property.PropertyType);
                }
                if (member is FieldInfo field)
                {
                    result["read"] = true; result["write"] = !field.IsInitOnly && !field.IsLiteral;
                    result["returnType"] = TypeReference(field.FieldType);
                    result["constant"] = field.IsLiteral ? Constant(field.GetRawConstantValue()) : null;
                }
                if (member is EventInfo ev)
                {
                    result["returnType"] = TypeReference(ev.EventHandlerType);
                    result["add"] = ev.GetAddMethod() != null; result["remove"] = ev.GetRemoveMethod() != null;
                }
            }
            catch (Exception ex) { result["idStatus"] = "unresolved"; result["id"] = null; result["error"] = ex.GetType().FullName + ": " + ex.Message; }
            return result;
        }

        // Inherited members from constructed generic bases must use the original
        // generic definition's signature, not the substituted contextual signature.
        static MemberInfo DefinitionMember(MemberInfo member)
        {
            Type owner = member.DeclaringType;
            if (owner == null) throw new NotSupportedException("Member without declaring type.");
            if (!owner.IsGenericType || owner.IsGenericTypeDefinition) return member;
            Type definition = owner.GetGenericTypeDefinition();
            return definition.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Single(m => m.MemberType == member.MemberType && m.MetadataToken == member.MetadataToken);
        }

        public static string XmlId(MemberInfo member)
        {
            member = DefinitionMember(member);
            string owner = DefinitionName(member.DeclaringType);
            string name = member.Name.Replace('.', '#').Replace('<', '{').Replace('>', '}');
            if (member is ConstructorInfo ctor) return "M:" + owner + "." + (ctor.IsStatic ? "#cctor" : "#ctor") + ParameterSuffix(ctor.GetParameters());
            if (member is MethodInfo method)
            {
                if (method.IsGenericMethod) name += "``" + method.GetGenericArguments().Length.ToString(CultureInfo.InvariantCulture);
                return "M:" + owner + "." + name + ParameterSuffix(method.GetParameters()) +
                    ((method.Name == "op_Implicit" || method.Name == "op_Explicit") ? "~" + TypeReference(method.ReturnType) : "");
            }
            if (member is PropertyInfo property) return "P:" + owner + "." + name + ParameterSuffix(property.GetIndexParameters());
            if (member is FieldInfo) return "F:" + owner + "." + name;
            if (member is EventInfo) return "E:" + owner + "." + name;
            throw new NotSupportedException("Unsupported XML documentation member: " + member.MemberType);
        }
        static string ParameterSuffix(ParameterInfo[] parameters) => parameters.Length == 0 ? "" : "(" + string.Join(",", parameters.Select(p => TypeReference(p.ParameterType))) + ")";
        static string DefinitionName(Type type)
        {
            if (type.IsGenericType && !type.IsGenericTypeDefinition) type = type.GetGenericTypeDefinition();
            if (type.FullName == null) throw new NotSupportedException("Type has no definition full name.");
            return type.FullName.Replace('+', '.');
        }
        public static string TypeReference(Type type)
        {
            if (type.IsByRef) return TypeReference(type.GetElementType()) + "@";
            if (type.IsPointer) return TypeReference(type.GetElementType()) + "*";
            if (type.IsArray) return TypeReference(type.GetElementType()) + (type.IsSZArray ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", type.GetArrayRank())) + "]");
            if (type.IsGenericParameter) return (type.DeclaringMethod != null ? "``" : "`") + type.GenericParameterPosition.ToString(CultureInfo.InvariantCulture);
            if (type.IsFunctionPointer) throw new NotSupportedException("Function pointer documentation encoding needs a separately verified profile.");
            if (!type.IsGenericType) return DefinitionName(type);
            string[] parts = type.GetGenericTypeDefinition().FullName.Split('+');
            Type[] args = type.GetGenericArguments(); int offset = 0;
            for (int i = 0; i < parts.Length; ++i)
            {
                int marker = parts[i].LastIndexOf('`');
                if (marker < 0) continue;
                int count = int.Parse(parts[i].Substring(marker + 1), CultureInfo.InvariantCulture);
                parts[i] = parts[i].Substring(0, marker) + "{" + string.Join(",", args.Skip(offset).Take(count).Select(TypeReference)) + "}";
                offset += count;
            }
            if (offset != args.Length) throw new NotSupportedException("Unresolved nested generic argument ownership.");
            return string.Join(".", parts);
        }
        static object[] GenericParameters(Type[] args) => args.Select(t => (object)new Dictionary<string, object> {
            ["name"] = t.Name, ["position"] = t.GenericParameterPosition, ["attributes"] = t.GenericParameterAttributes.ToString(),
            ["constraints"] = t.GetGenericParameterConstraints().Select(TypeReference).ToArray() }).ToArray();
        static object[] Parameters(ParameterInfo[] args) => args.Select(p => (object)new Dictionary<string, object> {
            ["name"] = p.Name, ["type"] = TypeReference(p.ParameterType), ["position"] = p.Position,
            ["isIn"] = p.IsIn, ["isOut"] = p.IsOut, ["isOptional"] = p.IsOptional,
            ["hasDefaultValue"] = p.HasDefaultValue, ["defaultValue"] = p.HasDefaultValue ? Constant(p.RawDefaultValue) : null,
            ["attributes"] = Attributes(p.GetCustomAttributesData()) }).ToArray();
        static object[] Attributes(IList<CustomAttributeData> attrs) => attrs.Select(a => (object)new Dictionary<string, object> {
            ["type"] = TypeReference(a.AttributeType),
            ["constructorArguments"] = a.ConstructorArguments.Select(AttributeValue).ToArray(),
            ["namedArguments"] = a.NamedArguments.Select(n => (object)new Dictionary<string, object> { ["name"] = n.MemberName, ["isField"] = n.IsField, ["value"] = AttributeValue(n.TypedValue) }).ToArray() }).ToArray();
        static object AttributeValue(CustomAttributeTypedArgument arg) => new Dictionary<string, object> {
            ["type"] = TypeReference(arg.ArgumentType), ["value"] = arg.Value is Type t ? TypeReference(t) :
                arg.Value is IList<CustomAttributeTypedArgument> list ? list.Select(AttributeValue).ToArray() : Constant(arg.Value) };
        static object Constant(object value)
        {
            if (value == null) return null;
            // Strings preserve 64-bit integer precision and invariant floating tokens
            // across JSON consumers; metadata records carry their declared type.
            if (value is IFormattable formatted) return formatted.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString();
        }
    }
}

