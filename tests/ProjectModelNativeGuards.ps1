param(
    [string]$WorkerPath = (Join-Path $PSScriptRoot '..\src\TiaMcp.Openness.V21\bin\Debug\net48\TiaMcp.Openness.V21.exe'),
    [string]$TiaApiDirectory = 'C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48'
)
$ErrorActionPreference = 'Stop'
# Run with Windows PowerShell (.NET Framework). Only detached ModelCapture and
# iterator logic is invoked: no TiaPortal instance, project or native API call.
Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public static class ProjectModelNativeGuardChecks
{
    private static Type captureType;
    private static MethodInfo add, skipped, bounded;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed class Sequence : IEnumerable<int>
    {
        public int Moves, Enumerators;
        public IEnumerator<int> GetEnumerator() { Enumerators++; return new Iterator(this); }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
        private sealed class Iterator : IEnumerator<int>
        {
            private readonly Sequence owner; private int index;
            internal Iterator(Sequence value) { owner = value; }
            public bool MoveNext() { owner.Moves++; return ++index <= 1000; }
            public int Current { get { return index; } }
            object IEnumerator.Current { get { return Current; } }
            public void Reset() { throw new NotSupportedException(); }
            public void Dispose() { }
        }
    }
    private static object Capture(int max, int depth)
    { return captureType.GetConstructors(Instance)[0].Invoke(new object[] { max, depth, 33554432, null, null }); }
    private static string Add(object capture, string path, string parent, bool native, bool visited)
    { return (string)add.Invoke(capture, new object[] { path, parent, native ? "group" : "folder", path, null, null, null, native, visited }); }
    private static int Used(object capture)
    {
        var budget = captureType.GetField("Budget", Instance).GetValue(capture);
        return (int)budget.GetType().GetField("Used", Instance).GetValue(budget);
    }
    private static bool Truncated(object capture)
    {
        var budget = captureType.GetField("Budget", Instance).GetValue(capture);
        return (bool)budget.GetType().GetField("Truncated", Instance).GetValue(budget);
    }
    private static IEnumerable<int> Items(Sequence sequence, object capture, string parent)
    { return (IEnumerable<int>)bounded.Invoke(null, new object[] { sequence, capture, parent }); }
    private static void Equal(object expected, object actual, string message)
    { if (!object.Equals(expected, actual)) throw new InvalidOperationException(message + ": expected=" + expected + ", actual=" + actual); }

    public static string Run(string worker, string api)
    {
        string workerDirectory = Path.GetDirectoryName(worker);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
        {
            string filename = new AssemblyName(args.Name).Name + ".dll";
            foreach (string directory in new[] { workerDirectory, api })
            { string file = Path.Combine(directory, filename); if (File.Exists(file)) return Assembly.LoadFrom(file); }
            return null;
        };
        var assembly = Assembly.LoadFrom(worker);
        captureType = assembly.GetType("TiaMcp.Openness.V21.OpennessSession+ModelCapture", true);
        add = captureType.GetMethod("Add", Instance); skipped = captureType.GetMethod("VisitSkipped", Instance);
        bounded = assembly.GetType("TiaMcp.Openness.V21.OpennessSession", true)
            .GetMethod("BoundedModelItems", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(typeof(int));

        var capture = Capture(5, 64); string root = Add(capture, "/root", null, true, false);
        string folder = Add(capture, "/root/folder", root, false, false); var sequence = new Sequence();
        foreach (int ignored in Items(sequence, capture, folder)) skipped.Invoke(capture, new object[0]);
        Equal(3, sequence.Moves, "Skipped native siblings stop before the fourth MoveNext");
        Equal(5, Used(capture), "Skipped native siblings consume exactly one visit");
        Equal(true, Truncated(capture), "Exhausted native traversal is explicitly partial");
        var remaining = new Sequence(); foreach (int ignored in Items(remaining, capture, folder)) throw new InvalidOperationException("Unexpected item after truncation");
        Equal(0, remaining.Enumerators, "No new sibling enumerator is opened after truncation");

        capture = Capture(6, 64); root = Add(capture, "/root", null, true, false);
        folder = Add(capture, "/root/folder", root, false, false); var outer = new Sequence(); var inner = new Sequence();
        foreach (int child in Items(outer, capture, folder))
        {
            string childId = Add(capture, "/root/folder/child" + child, folder, true, false);
            foreach (int ignored in Items(inner, capture, childId)) skipped.Invoke(capture, new object[0]);
        }
        Equal(1, outer.Moves, "Parent does not fetch a next sibling after nested exhaustion");
        Equal(3, inner.Moves, "Child obeys shared remaining visits"); Equal(6, Used(capture), "Shared visits remain bounded");

        for (int depth = 1; depth <= 2; depth++)
        {
            capture = Capture(50, depth); root = Add(capture, "/root", null, true, false);
            folder = Add(capture, "/root/folder", root, false, false);
            string parent = depth == 1 ? folder : Add(capture, "/root/folder/device", folder, true, false);
            sequence = new Sequence(); foreach (int ignored in Items(sequence, capture, parent)) throw new InvalidOperationException("Unexpected over-depth node");
            Equal(0, sequence.Enumerators, "Actual parent depth prevents even opening over-depth enumeration");
            Equal(true, Truncated(capture), "Depth bound is explicit partial coverage");
            Equal(null, Add(capture, "/too-deep", parent, true, false), "No node retained after depth truncation");
        }
        capture = Capture(50, 64); root = Add(capture, "/root", null, true, false);
        folder = Add(capture, "/root/units", root, true, false); skipped.Invoke(capture, new object[0]);
        Add(capture, "/root/units/A", folder, true, true);
        Equal(3, Used(capture), "Already visited SoftwareScopes unit is not charged twice");
        return "PASS 5 compiled native model guard checks: skipped visits, nested exhaustion, maxDepth1, maxDepth2, unit accounting. No TIA instance started.";
    }
}
'@
[ProjectModelNativeGuardChecks]::Run((Resolve-Path -LiteralPath $WorkerPath).Path, $TiaApiDirectory)
