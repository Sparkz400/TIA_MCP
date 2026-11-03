using System;
using System.Collections.Generic;

namespace TiaMcp.Model
{
    // An instance belongs to one continuously held native ExclusiveAccess lease.
    // Never serialized, never retained across leases, never populated by a caller.
    internal sealed class HmiLeaseObservations
    {
        private readonly Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
        internal T Get<T>(string key, Func<T> observe)
        {
            if (values.TryGetValue(key, out var value)) return (T)value;
            T result = observe(); // Failed/partial observations are never cached.
            values.Add(key, result);
            return result;
        }
        internal void Invalidate() => values.Clear();
    }
}
