#nullable disable
using System;

namespace TiaMcp.Simulation
{
    public static class SimulationDiscoveryWait
    {
        public const int BudgetMs = 30000;
        public const int MaximumQueries = 61;
        public const int RequiredEmptySnapshots = 2;

        // Only read-only discovery is repeated. Identity/OFF guards run around
        // every snapshot; errors propagate immediately. No endpoint is ignored
        // or selected, and no state/credential operation is accepted here.
        public static bool ObserveEmpty(Action verifyOwnedOff, Func<bool> queryEmpty,
            Action<int, bool, int, long> record, Func<long> clock, Action<int> delay)
        {
            long start = clock();
            Func<long> elapsed = () => Math.Max(0L, clock() - start);
            int consecutive = 0;
            for (int attempt = 1; attempt <= MaximumQueries; attempt++)
            {
                verifyOwnedOff();
                if (elapsed() >= BudgetMs) return false;
                bool empty = queryEmpty();
                verifyOwnedOff();
                consecutive = empty ? consecutive + 1 : 0;
                record(attempt, empty, consecutive, elapsed());
                if (elapsed() >= BudgetMs) return false;
                if (consecutive >= RequiredEmptySnapshots) return true;
                if (attempt == MaximumQueries) return false;
                long remaining = BudgetMs - elapsed();
                if (remaining <= 0) return false;
                delay((int)Math.Min(500L, remaining));
            }
            return false;
        }
    }
}
