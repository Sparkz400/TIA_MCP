using System.Text.Json;
using Opc.Ua;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.OpcUa.Runtime;

internal sealed partial class OpcUaRuntimeClient
{
    private static (string?, object?, string?) ArrayValue(Variant variant, int? rank, uint[]? declared)
    {
        string? elementEncoding = OpcUaCaptureContracts.ScalarEncoding(variant.TypeInfo?.BuiltInType.ToString());
        if (elementEncoding is null) return (null, null, "UaArrayElementUnsupported");
        Array? source;
        int[]? dimensions;
        if (variant.Value is Matrix matrix)
        {
            source = matrix.Elements;
            dimensions = matrix.Dimensions;
        }
        else if (variant.Value is Array array)
        {
            source = array;
            if (Enumerable.Range(0, array.Rank).Any(i => array.GetLowerBound(i) != 0))
                return (null, null, "UaArrayShapeMismatch");
            dimensions = Enumerable.Range(0, array.Rank).Select(array.GetLength).ToArray();
        }
        else if (variant.Value is null) { source = null; dimensions = null; }
        else return (null, null, "UaArrayShapeMismatch");
        if (!OpcUaCaptureContracts.ArrayShapeMatches(rank, declared, dimensions, source?.Length))
            return (null, null, "UaArrayShapeMismatch");
        if (source is null) return ("array", new OpcUaRuntimeArray(null, elementEncoding, null), null);
        var elements = new object?[source.Length];
        int index = 0, bytes = 0;
        foreach (object? original in source)
        {
            var (encoding, value, error) = Scalar(original);
            bool nullable = original is null && variant.TypeInfo?.BuiltInType is BuiltInType.String or BuiltInType.ByteString;
            if (error is not null || !nullable && encoding != elementEncoding)
                return (null, null, "UaArrayElementUnsupported");
            bytes += JsonSerializer.SerializeToUtf8Bytes(value).Length + 1;
            if (bytes > MaxMessageBytes) return (null, null, "UaArrayValueLimit");
            elements[index++] = value;
        }
        return ("array", new OpcUaRuntimeArray(dimensions, elementEncoding, elements), null);
    }
}
