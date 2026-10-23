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

    // PLCSIM Advanced V8's Runtime Manager publishes one all-zero Softbus entry while no
    // instance is registered (docs/verification/native-stop-20261023.md). Only that exact
    // entry, recorded before registration, may be excluded later; anything else still counts.
    public static class SimulationRuntimePlaceholder
    {
        public const string ZeroAddress = "00-00-00-00-00-00";

        public readonly struct Device
        {
            public Device(string name, string address, string macAddress, string deviceSeries)
            { Name = name; Address = address; MacAddress = macAddress; DeviceSeries = deviceSeries; }
            public string Name { get; }
            public string Address { get; }
            public string MacAddress { get; }
            public string DeviceSeries { get; }
        }

        public static bool IsCandidate(Device device) =>
            device.Address == ZeroAddress && device.MacAddress == ZeroAddress && string.IsNullOrEmpty(device.DeviceSeries);

        // Baseline taken with zero registered instances: "empty", "placeholder" (exactly one
        // candidate) or null when anything else is visible.
        public static string ClassifyBaseline(Device[] devices) =>
            devices == null || devices.Length == 0 ? "empty"
            : devices.Length == 1 && IsCandidate(devices[0]) ? "placeholder"
            : null;

        public static bool Matches(Device baseline, Device device) =>
            IsCandidate(baseline) && device.Name == baseline.Name && device.Address == baseline.Address &&
            device.MacAddress == baseline.MacAddress && device.DeviceSeries == baseline.DeviceSeries;

        // The baseline proved one placeholder, so at most one entry may be excluded: the index of the
        // single exact match, or -1 when there is no baseline, no match or more than one match.
        public static int SingleMatchIndex(Device[] devices, Device? baseline)
        {
            if (baseline == null) return -1;
            int index = -1;
            for (int i = 0; i < devices.Length; i++)
            {
                if (!Matches(baseline.Value, devices[i])) continue;
                if (index >= 0) return -1;
                index = i;
            }
            return index;
        }

        public enum Exclusion { None, Single, RefuseTruncated }

        // A read that hit its limit is never narrowed: with a match it must be refused instead.
        public static Exclusion Decide(Device[] devices, Device? baseline, bool truncated, out int index)
        {
            index = SingleMatchIndex(devices, baseline);
            return index < 0 ? Exclusion.None : truncated ? Exclusion.RefuseTruncated : Exclusion.Single;
        }

        public const long BaselineLifetimeMs = 300000;

        // One provisioning attempt in the same TIA session, project and PLC, within the lifetime.
        public static bool BaselineApplies(long ageMs, bool sameSession, bool sameProject, bool samePlc) =>
            sameSession && sameProject && samePlc && ageMs >= 0 && ageMs <= BaselineLifetimeMs;
    }
}
