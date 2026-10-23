using System;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Simulator.V8
{
    /// <summary>Pure, type-preserving comparison shared by scenario execution and tests.</summary>
    internal static class SimulationAssertions
    {
        internal static bool Compare(string type, JToken actual, JToken expected, string operation, double tolerance)
        {
            if (tolerance < 0 || double.IsNaN(tolerance) || double.IsInfinity(tolerance))
                throw new ArgumentException("Tolerance must be finite and nonnegative.");
            int comparison;
            if (type == "float" || type == "double")
            {
                double observed = (double)actual; double wanted = (double)expected;
                if (double.IsNaN(observed) || double.IsInfinity(observed) || double.IsNaN(wanted) || double.IsInfinity(wanted))
                    throw new ArgumentException("Non-finite PLC values cannot satisfy numeric assertions.");
                comparison = Math.Abs(observed - wanted) <= tolerance ? 0 : observed.CompareTo(wanted);
            }
            else if (type == "bool" || type == "string" || type == "wstring")
            {
                if (operation != "eq" && operation != "ne") throw new ArgumentException("Only eq/ne compare strings and booleans.");
                comparison = JToken.DeepEquals(actual, expected) ? 0 : 1;
            }
            else
            {
                // Decimal retains all signed/unsigned 64-bit integer bits; double does not.
                decimal delta = (decimal)actual - (decimal)expected;
                comparison = tolerance >= (double)decimal.MaxValue || Math.Abs(delta) <= (decimal)tolerance ? 0 : Math.Sign(delta);
            }
            switch (operation)
            {
                case "eq": return comparison == 0;
                case "ne": return comparison != 0;
                case "gt": return comparison > 0;
                case "gte": return comparison >= 0;
                case "lt": return comparison < 0;
                case "lte": return comparison <= 0;
                default: throw new ArgumentException("Unknown assertion operator.");
            }
        }
    }
}
