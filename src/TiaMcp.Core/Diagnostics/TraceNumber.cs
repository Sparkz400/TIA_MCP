using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Rejects decimal overflow, underflow and rounding instead of changing measurement evidence.</summary>
internal static class TraceNumber
{
    internal static bool HasIntervalGap(decimal previous, decimal time, decimal interval)
        => 2 * (Scaled(time) - Scaled(previous)) > 3 * Scaled(interval);

    internal static decimal SubtractTime(decimal time, decimal anchor)
    {
        decimal result = time - anchor;
        if (Scaled(result) != Scaled(time) - Scaled(anchor))
            throw new BridgeException("MeasurementFormatUnsupported", "Event alignment would lose timestamp precision; choose an anchor whose relative timestamps can be represented exactly.");
        return result;
    }

    // Integer units of 1e-28 preserve every decimal bit through subtraction and the 3/2 gap threshold.
    private static BigInteger Scaled(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        BigInteger coefficient = (uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
        return (bits[3] < 0 ? -coefficient : coefficient) * BigInteger.Pow(10, 28 - ((bits[3] >> 16) & 0xff));
    }

    internal static decimal Parse(string text)
    {
        if (text.Length > 128 || !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out decimal value) || Canonical(text) != Canonical(value.ToString(CultureInfo.InvariantCulture)))
            throw new BridgeException("MeasurementFormatUnsupported", "Numeric value cannot be represented exactly in the supported decimal range/precision; preserve it as explicitly unavailable or use another qualified decoder.");
        return value;
    }
    private static string Canonical(string text)
    {
        var m = Regex.Match(text, @"^([+-]?)([0-9]*)(?:\.([0-9]*))?(?:[eE]([+-]?[0-9]+))?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!m.Success || m.Groups[2].Length + m.Groups[3].Length == 0
            || m.Groups[4].Success && !int.TryParse(m.Groups[4].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) return "invalid";
        long exponent = (m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0L) - m.Groups[3].Length;
        string digits = (m.Groups[2].Value + m.Groups[3].Value).TrimStart('0');
        if (digits.Length == 0) return "0";
        int length = digits.Length; digits = digits.TrimEnd('0'); exponent += length - digits.Length;
        return (m.Groups[1].Value == "-" ? "-" : "") + digits + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
