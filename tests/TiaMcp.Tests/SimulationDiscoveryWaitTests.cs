using TiaMcp.Simulation;

namespace TiaMcp.Tests;

internal static class SimulationDiscoveryWaitTests
{
    internal static Task TransientEntriesAndStableAbsence()
    {
        long now = 0; int guards = 0;
        var snapshots = new Queue<bool>([false, true, false, true, true]);
        var evidence = new List<(int Attempt, bool Empty, int Consecutive)>();
        bool clear = SimulationDiscoveryWait.ObserveEmpty(() => guards++, () => snapshots.Dequeue(),
            (attempt, empty, consecutive, elapsed) => evidence.Add((attempt, empty, consecutive)),
            () => now, delay => now += delay);
        Check.Equal(true, clear); Check.Equal(10, guards); Check.Equal(5, evidence.Count);
        Check.Equal(0, evidence[2].Consecutive); Check.Equal(2, evidence[4].Consecutive);
        Check.Equal(2000L, now); Check.Equal(false, evidence[0].Empty);
        return Task.CompletedTask;
    }

    internal static Task PersistentDevicesAndLateSnapshots()
    {
        long now = 0; int queries = 0;
        bool clear = SimulationDiscoveryWait.ObserveEmpty(() => { }, () => { queries++; return false; },
            (_, _, _, _) => { }, () => now, delay => now += delay);
        Check.Equal(false, clear); Check.Equal(SimulationDiscoveryWait.BudgetMs, (int)now);
        Check.Equal(true, queries <= SimulationDiscoveryWait.MaximumQueries);
        now = 0; queries = 0;
        clear = SimulationDiscoveryWait.ObserveEmpty(() => { }, () => { queries++; now += 16000; return true; },
            (_, _, _, _) => { }, () => now, delay => now += delay);
        Check.Equal(false, clear); Check.Equal(2, queries);
        now = 0; queries = 0;
        clear = SimulationDiscoveryWait.ObserveEmpty(() => { }, () => { queries++; return false; },
            (_, _, _, _) => { }, () => now, _ => { });
        Check.Equal(false, clear); Check.Equal(SimulationDiscoveryWait.MaximumQueries, queries);
        return Task.CompletedTask;
    }

    internal static Task RuntimePlaceholderIsExactAndNarrow()
    {
        var zero = SimulationRuntimePlaceholder.ZeroAddress;
        var placeholder = new SimulationRuntimePlaceholder.Device("Accessible device", zero, zero, "");
        var cpu = new SimulationRuntimePlaceholder.Device("plc_1", "192.168.0.1", "02-00-00-00-00-01", "S7-1500");
        Check.Equal("empty", SimulationRuntimePlaceholder.ClassifyBaseline([]));
        Check.Equal("placeholder", SimulationRuntimePlaceholder.ClassifyBaseline([placeholder]));
        Check.Equal(null, SimulationRuntimePlaceholder.ClassifyBaseline([placeholder, placeholder]));
        Check.Equal(null, SimulationRuntimePlaceholder.ClassifyBaseline([cpu]));
        foreach (var near in new[] {
            new SimulationRuntimePlaceholder.Device("Accessible device", "192.168.0.1", zero, ""),
            new SimulationRuntimePlaceholder.Device("Accessible device", zero, "02-00-00-00-00-01", ""),
            new SimulationRuntimePlaceholder.Device("Accessible device", zero, zero, "S7-1500") })
        {
            Check.Equal(null, SimulationRuntimePlaceholder.ClassifyBaseline([near]));
            Check.Equal(false, SimulationRuntimePlaceholder.Matches(placeholder, near));
        }
        Check.Equal(false, SimulationRuntimePlaceholder.Matches(placeholder, new SimulationRuntimePlaceholder.Device("Teilnehmer", zero, zero, "")));
        Check.Equal(false, SimulationRuntimePlaceholder.Matches(cpu, cpu)); // only a placeholder can be a baseline
        Check.Equal(0, SimulationRuntimePlaceholder.SingleMatchIndex([placeholder, cpu], placeholder));
        Check.Equal(1, SimulationRuntimePlaceholder.SingleMatchIndex([cpu, placeholder], placeholder));
        Check.Equal(-1, SimulationRuntimePlaceholder.SingleMatchIndex([placeholder, cpu], null));
        Check.Equal(-1, SimulationRuntimePlaceholder.SingleMatchIndex([cpu], placeholder));
        // Two copies prove more than the baseline did: exclude neither, so [P, P, cpu] stays ambiguous.
        Check.Equal(-1, SimulationRuntimePlaceholder.SingleMatchIndex([placeholder, placeholder, cpu], placeholder));
        // A null series later never matches a recorded empty series (and vice versa).
        var nullSeries = new SimulationRuntimePlaceholder.Device("Accessible device", zero, zero, null);
        Check.Equal(true, SimulationRuntimePlaceholder.IsCandidate(nullSeries));
        Check.Equal(-1, SimulationRuntimePlaceholder.SingleMatchIndex([nullSeries, cpu], placeholder));
        // Truncated reads: refuse only when an exclusion would apply; otherwise behave as before.
        Check.Equal(SimulationRuntimePlaceholder.Exclusion.Single, SimulationRuntimePlaceholder.Decide([placeholder, cpu], placeholder, false, out int single));
        Check.Equal(0, single);
        Check.Equal(SimulationRuntimePlaceholder.Exclusion.RefuseTruncated, SimulationRuntimePlaceholder.Decide([cpu, placeholder, cpu], placeholder, true, out _));
        Check.Equal(SimulationRuntimePlaceholder.Exclusion.None, SimulationRuntimePlaceholder.Decide([cpu, cpu, cpu], placeholder, true, out _));
        Check.Equal(SimulationRuntimePlaceholder.Exclusion.None, SimulationRuntimePlaceholder.Decide([placeholder, placeholder, cpu], placeholder, true, out _));
        Check.Equal(SimulationRuntimePlaceholder.Exclusion.None, SimulationRuntimePlaceholder.Decide([placeholder, cpu], null, false, out _));
        return Task.CompletedTask;
    }

    internal static Task RuntimePlaceholderBaselineLifetime()
    {
        long limit = SimulationRuntimePlaceholder.BaselineLifetimeMs;
        Check.Equal(true, SimulationRuntimePlaceholder.BaselineApplies(0, true, true, true));
        Check.Equal(true, SimulationRuntimePlaceholder.BaselineApplies(limit, true, true, true));
        Check.Equal(false, SimulationRuntimePlaceholder.BaselineApplies(limit + 1, true, true, true));
        Check.Equal(false, SimulationRuntimePlaceholder.BaselineApplies(-1, true, true, true));
        Check.Equal(false, SimulationRuntimePlaceholder.BaselineApplies(0, false, true, true));
        Check.Equal(false, SimulationRuntimePlaceholder.BaselineApplies(0, true, false, true));
        Check.Equal(false, SimulationRuntimePlaceholder.BaselineApplies(0, true, true, false));
        return Task.CompletedTask;
    }

    internal static async Task GuardAndReadFailuresNeverRetry()
    {
        foreach (string failure in new[] { "before", "read", "after", "record" })
        {
            int guards = 0, queries = 0, waits = 0;
            await Check.ThrowsAsync<InvalidOperationException>(() => {
                SimulationDiscoveryWait.ObserveEmpty(() => {
                    guards++;
                    if (failure == "before" || (failure == "after" && guards == 2)) throw new InvalidOperationException();
                }, () => { queries++; if (failure == "read") throw new InvalidOperationException(); return true; },
                    (_, _, _, _) => { if (failure == "record") throw new InvalidOperationException(); },
                    () => 0, _ => waits++);
                return Task.CompletedTask;
            });
            Check.Equal(failure == "before" ? 0 : 1, queries); Check.Equal(0, waits);
        }
    }
}
