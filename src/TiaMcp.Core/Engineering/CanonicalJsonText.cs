#nullable disable
using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace TiaMcp.Core.Engineering
{
    // Shared with the net48 Openness worker: RFC 8785 (JCS) string and number text, so the Host and the
    // worker hash the same bytes. Each runtime walks its own JSON tree and refuses what these return null for.
    public static class CanonicalJsonText
    {
        // Escapes only '"', '\' and U+0000-U+001F (short forms where JSON has them, else lowercase \u00xx);
        // everything else, surrogate pairs included, stays literal. False for a lone surrogate.
        public static bool TryAppendString(StringBuilder output, string value)
        {
            output.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\t': output.Append("\\t"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\r': output.Append("\\r"); break;
                    default:
                        if (c < ' ') output.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                        else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) output.Append(c).Append(value[++i]);
                        else if (char.IsSurrogate(c)) return false;
                        else output.Append(c);
                        break;
                }
            }
            output.Append('"');
            return true;
        }

        // ECMAScript Number::toString: the fewest significant digits that round back to the same double, the
        // closest such digits when several do, computed exactly so .NET Framework and .NET give the same text.
        // Null for NaN and infinities.
        public static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return null;
            if (value == 0) return "0";
            long bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
            int biased = (int)(bits >> 52);
            long fraction = bits & 0xFFFFFFFFFFFFFL;
            BigInteger m = biased == 0 ? fraction : fraction | 1L << 52;
            int e = (biased == 0 ? 1 : biased) - 1075;
            // value = m * 2^e; it rounds back from (low, high) in units of 2^(e-2), the ends only for an even m.
            // Just above a power of two the gap below is half the gap above.
            BigInteger low = biased > 1 && fraction == 0 ? 4 * m - 1 : 4 * m - 2, high = 4 * m + 2;
            int k = (int)Math.Floor(Math.Log10(Math.Abs(value))); // value lies in [10^k, 10^(k+1))
            if (Compare(1, k, 4 * m, e - 2) > 0) k--;
            else if (Compare(1, k + 1, 4 * m, e - 2) <= 0) k++;
            // Once some digits fit, more digits fit too, so search for the fewest; 17 always fit.
            int fewest = 1, most = 17;
            while (fewest < most)
            {
                int middle = (fewest + most) / 2;
                if (Digits(m, e, low, high, k - middle + 1) == null) fewest = middle + 1; else most = middle;
            }
            BigInteger digits = Digits(m, e, low, high, k - fewest + 1).Value;
            return (value < 0 ? "-" : "") + Layout(digits.ToString(CultureInfo.InvariantCulture), k - fewest + 1);
        }

        // The digits c with value ~ c * 10^q that round back to value, the closer (then the even) one when both
        // neighbours do; null when neither does.
        private static BigInteger? Digits(BigInteger m, int e, BigInteger low, BigInteger high, int q)
        {
            BigInteger numerator = m, denominator = BigInteger.One;
            if (e >= 0) numerator <<= e; else denominator <<= -e;
            if (q >= 0) denominator *= Ten(q); else numerator *= Ten(-q);
            BigInteger below = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
            bool even = m.IsEven;
            bool belowFits = !below.IsZero && RoundsBack(below, q, low, high, e, even);
            bool aboveFits = !remainder.IsZero && RoundsBack(below + 1, q, low, high, e, even);
            if (belowFits && aboveFits)
            {
                int closer = Compare(2 * below + 1, q, 8 * m, e - 2); // their midpoint against value
                return closer < 0 || closer == 0 && !below.IsEven ? below + 1 : below;
            }
            return belowFits ? below : aboveFits ? below + 1 : (BigInteger?)null;
        }

        private static bool RoundsBack(BigInteger c, int q, BigInteger low, BigInteger high, int e, bool even)
        {
            int above = Compare(c, q, low, e - 2), below = Compare(c, q, high, e - 2);
            return (above > 0 || above == 0 && even) && (below < 0 || below == 0 && even);
        }

        // Compares c * 10^q with n * 2^p exactly.
        private static int Compare(BigInteger c, int q, BigInteger n, int p)
        {
            if (q >= 0) c *= Ten(q); else n *= Ten(-q);
            if (p >= 0) n <<= p; else c <<= -p;
            return c.CompareTo(n);
        }

        private static BigInteger Ten(int n) => n < Powers.Ten.Length ? Powers.Ten[n] : BigInteger.Pow(10, n);

        // Built on the first number only; doubles need 10^0 to 10^341.
        private static class Powers
        {
            internal static readonly BigInteger[] Ten = Build();
            private static BigInteger[] Build()
            {
                var powers = new BigInteger[342]; powers[0] = BigInteger.One;
                for (int i = 1; i < powers.Length; i++) powers[i] = powers[i - 1] * 10;
                return powers;
            }
        }

        private static string Layout(string digits, int q)
        {
            int trimmed = digits.Length;
            while (digits[trimmed - 1] == '0') trimmed--;
            q += digits.Length - trimmed; digits = digits.Substring(0, trimmed);
            int k = digits.Length, n = k + q; // value = 0.digits * 10^n
            if (k <= n && n <= 21) return digits + new string('0', n - k);
            if (0 < n && n <= 21) return digits.Substring(0, n) + "." + digits.Substring(n);
            if (-6 < n && n <= 0) return "0." + new string('0', -n) + digits;
            string exponent = (n - 1 < 0 ? "-" : "+") + Math.Abs(n - 1).ToString(CultureInfo.InvariantCulture);
            return (k == 1 ? digits : digits.Substring(0, 1) + "." + digits.Substring(1)) + "e" + exponent;
        }
    }
}
