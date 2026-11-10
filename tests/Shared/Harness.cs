// Helpers that were repeated verbatim across the test programs; compiled into every net10 test project by
// tests/Directory.Build.props. A file's own local function or member of the same name still wins.
global using static TestHarness;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;

internal static class TestHarness
{
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Runs the registered tests in order, printing "PASS name" to stdout or "FAIL name: exception" to
    /// stderr (or stdout), and returns the number of failures. The caller prints its own summary line.</summary>
    internal static int RunTests(IEnumerable<(string Name, Action Run)> tests, bool failuresToStderr)
    {
        int failed = 0;
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; (failuresToStderr ? Console.Error : Console.Out).WriteLine("FAIL " + name + ": " + error); }
        }
        return failed;
    }
}
