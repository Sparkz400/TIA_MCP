namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private sealed class ScanBudget
        {
            internal int Used;
            internal bool Truncated;
            private readonly int _maximum;
            internal ScanBudget(int maximum) { _maximum = maximum; }
            internal bool Take() { if (Used >= _maximum) { Truncated = true; return false; } Used++; return true; }
        }
    }
}
