using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TiaMcp.Indexing;
using Store = TiaMcp.Indexing.ProjectIndexStore;

var tests = new List<object>();
var failures = new List<string>();
var benchmarks = new List<object>();

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

void Error(string code, Action action)
{
    try { action(); }
    catch (Store.IndexException exception)
    {
        Check(exception.Code == code, $"Expected {code}, received {exception.Code}.");
        return;
    }
    throw new InvalidOperationException($"Expected {code}, operation succeeded.");
}

void Test(string name, Action action)
{
    var timer = Stopwatch.StartNew();
    try
    {
        action();
        tests.Add(new { name, status = "passed", elapsedMilliseconds = timer.Elapsed.TotalMilliseconds });
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add(name);
        tests.Add(new { name, status = "failed", elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, error = exception.ToString() });
        Console.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

Store.Row Row(string path, string name = "Shared", string kind = "block", string plc = "/PLC_A", string scope = "/PLC_A/units/Unit_A", string? payload = null) =>
    new(path, kind, name, plc, scope, payload ?? JsonSerializer.Serialize(new { path, name, kind, plc, scope }));

Store.Snapshot Capture(Store store, IEnumerable<Store.Row>? rows = null, int lifetime = 300, bool complete = true, string[]? warnings = null) =>
    store.Capture(rows ?? [Row("/PLC_A/blocks/A"), Row("/PLC_A/blocks/B"), Row("/PLC_A/blocks/C")],
        @"C:\TiaScratch\Project.ap21", "revision-1", complete, warnings ?? [], lifetime);

Store.Page Query(Store store, Store.Snapshot snapshot, int max = 2, string? cursor = null, string? text = null,
    string? kind = null, string? plc = null, string? scope = null) =>
    store.Query(snapshot.Id, text!, kind!, plc!, scope!, max, cursor!);

string RewriteCursor(string cursor, Func<string[], string[]> change)
{
    var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
    return Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(':', change(parts))));
}

Test("Capture sorts canonical paths and isolates source/warning arrays", () =>
{
    var store = new Store();
    var source = new[] { Row("/z"), Row("/a") };
    var warnings = new[] { "Unit enumeration incomplete" };
    var snapshot = Capture(store, source, complete: false, warnings: warnings);
    source[0] = Row("/changed");
    warnings[0] = "changed";
    var result = Query(store, snapshot);
    Check(result.Rows.Select(row => row.Path).SequenceEqual(["/a", "/z"]), "Capture retained mutable source alias or failed ordinal sorting.");
    Check(!snapshot.ScopeComplete && snapshot.Warnings[0] == "Unit enumeration incomplete", "Completeness evidence changed.");
    Check(snapshot.Revision == "revision-1" && snapshot.ProjectPath.EndsWith("Project.ap21"), "Snapshot provenance lost.");
});

Test("Captured row and warning collections reject mutation through IList", () =>
{
    var store = new Store();
    var snapshot = Capture(store, warnings:["Unverified external edit"]);
    var rowMutationRejected = false;
    var warningMutationRejected = false;
    try { ((IList<Store.Row>)snapshot.Rows)[0] = Row("/changed"); }
    catch (NotSupportedException) { rowMutationRejected = true; }
    try { ((IList<string>)snapshot.Warnings)[0] = "changed"; }
    catch (NotSupportedException) { warningMutationRejected = true; }
    Check(rowMutationRejected && warningMutationRejected, "Snapshot collections permitted mutation.");
    Check(Query(store, snapshot).Rows[0].Path == "/PLC_A/blocks/A" && snapshot.Warnings[0] == "Unverified external edit", "Snapshot changed despite read-only contract.");
});

Test("Duplicate names across PLCs and software-unit namespaces remain distinct", () =>
{
    var store = new Store();
    var rows = new[] {
        Row("/PLC_A/units/A/blocks/Shared", plc:"/PLC_A", scope:"/PLC_A/units/A"),
        Row("/PLC_A/units/B/blocks/Shared", plc:"/PLC_A", scope:"/PLC_A/units/B"),
        Row("/PLC_B/blocks/Shared", plc:"/PLC_B", scope:"/PLC_B")
    };
    var snapshot = Capture(store, rows);
    Check(Query(store, snapshot, text: "Shared").Total == 3, "Duplicate-name entries were collapsed.");
    var scoped = Query(store, snapshot, plc: "/PLC_A", scope: "/PLC_A/units/B");
    Check(scoped.Total == 1 && scoped.Rows[0].Path == rows[1].Path, "Exact namespace filtering failed.");
    Check(Query(store, snapshot, plc: "/plc_a").Total == 0, "Canonical PLC path incorrectly became case insensitive.");
});

Test("Unicode metadata and ordinal case-insensitive search preserve exact identities", () =>
{
    var store = new Store();
    var snapshot = Capture(store, [Row("/PLC/Größe/Überwachung", "Überwachung_日本_温度_🚀"), Row("/PLC/a"), Row("/PLC/A")]);
    var found = Query(store, snapshot, text: "überWACHUNG");
    Check(found.Total == 1 && found.Rows[0].Name.Contains("日本_温度_🚀"), "Unicode search or metadata lost.");
    Check(Query(store, snapshot, text: "温度").Total == 1, "Non-Latin search failed.");
    Check(Query(store, snapshot, max: 10).Total == 3, "Ordinal distinct canonical paths collapsed.");
});

Test("Kind filtering and unknown filters are exact", () =>
{
    var store = new Store();
    var snapshot = Capture(store, [Row("/A", kind:"block"), Row("/B", kind:"tagTable")]);
    Check(Query(store, snapshot, kind:"tagTable").Rows.Single().Path == "/B", "Kind filter failed.");
    Check(Query(store, snapshot, kind:"BLOCK").Total == 0, "Kind filter incorrectly case folded.");
    var empty = Query(store, snapshot, text:"nonexistent");
    Check(empty.Total == 0 && empty.Rows.Length == 0 && empty.NextCursor is null && empty.PayloadBytes == 0, "Empty query pagination invalid.");
});

Test("Duplicate canonical paths fail without replacing prior snapshot", () =>
{
    var store = new Store();
    var prior = Capture(store);
    Error("AmbiguousIndexIdentity", () => Capture(store, [Row("/same", "A"), Row("/same", "B")]));
    Check(store.Get(prior.Id) == prior, "Failed capture damaged an earlier snapshot.");
});

Test("Malformed rows reject null row, empty identity and null payload", () =>
{
    var store = new Store();
    Error("InvalidIndexRow", () => Capture(store, [null!]));
    Error("InvalidIndexRow", () => Capture(store, [Row("")]));
    Error("InvalidIndexRow", () => Capture(store, [new Store.Row("/A", "block", "A", "/PLC", "/PLC", null!)]));
});

Test("Cursor traversal can change page size without duplicate or skipped rows", () =>
{
    var store = new Store();
    var snapshot = Capture(store, Enumerable.Range(0, 13).Reverse().Select(i => Row($"/{i:D2}")));
    var paths = new List<string>();
    string? cursor = null;
    var turn = 0;
    do
    {
        var page = Query(store, snapshot, max: ++turn % 2 == 0 ? 4 : 2, cursor: cursor);
        Check(page.Offset == paths.Count, "Cursor offset drifted.");
        paths.AddRange(page.Rows.Select(row => row.Path));
        cursor = page.NextCursor;
    } while (cursor is not null);
    Check(paths.SequenceEqual(Enumerable.Range(0, 13).Select(i => $"/{i:D2}")), "Cursor traversal was incoherent.");
});

Test("Cursor cannot be reused for changed text, kind, PLC or scope filters", () =>
{
    var store = new Store();
    var snapshot = Capture(store);
    var cursor = Query(store, snapshot).NextCursor!;
    Error("InvalidCursor", () => Query(store, snapshot, cursor: cursor, text: "Shared"));
    Error("InvalidCursor", () => Query(store, snapshot, cursor: cursor, kind: "block"));
    Error("InvalidCursor", () => Query(store, snapshot, cursor: cursor, plc: "/PLC_A"));
    Error("InvalidCursor", () => Query(store, snapshot, cursor: cursor, scope: "/PLC_A/units/Unit_A"));
    Error("InvalidCursor", () => Query(store, snapshot, cursor: cursor, text: ""));
});

Test("Cursor rejects wrong snapshots and a new worker lifetime", () =>
{
    var first = new Store();
    var snapshot = Capture(first);
    var cursor = Query(first, snapshot).NextCursor!;
    var nextSnapshot = Capture(first);
    Error("InvalidCursor", () => Query(first, nextSnapshot, cursor: cursor));
    var newWorker = new Store();
    Error("IndexNotFound", () => Query(newWorker, snapshot, cursor: cursor));
    var newSnapshot = Capture(newWorker);
    var moved = RewriteCursor(cursor, parts => { parts[0] = newSnapshot.Id; return parts; });
    Error("InvalidCursor", () => Query(newWorker, newSnapshot, cursor: moved));
});

Test("Authenticated cursor rejects offset, query digest, signature and envelope tampering", () =>
{
    var store = new Store();
    var snapshot = Capture(store);
    var cursor = Query(store, snapshot).NextCursor!;
    foreach (var position in new[] { "0", "-1", "2147483648", "99999" })
        Error("InvalidCursor", () => Query(store, snapshot, cursor: RewriteCursor(cursor, parts => { parts[2] = position; return parts; })));
    Error("InvalidCursor", () => Query(store, snapshot, cursor: RewriteCursor(cursor, parts => { parts[1] = new string('0', 64); return parts; })));
    Error("InvalidCursor", () => Query(store, snapshot, cursor: RewriteCursor(cursor, parts => { parts[3] = new string('A', parts[3].Length); return parts; })));
    foreach (var malformed in new[] { "", "%%%", Convert.ToBase64String(Encoding.UTF8.GetBytes("a:b:c")), new string('A', 513) })
        Error("InvalidCursor", () => Query(store, snapshot, cursor: malformed));
    Check(Query(store, snapshot, cursor: cursor).Rows.Single().Path == "/PLC_A/blocks/C", "Tampering attempts altered stored cursor state.");
});

Test("Cursor replay reads the same immutable page after query-cache eviction", () =>
{
    var store = new Store();
    var snapshot = Capture(store);
    var cursor = Query(store, snapshot).NextCursor!;
    var expected = Query(store, snapshot, cursor: cursor).Rows.Select(row => row.Path).ToArray();
    for (var i = 0; i < 20; ++i) Query(store, snapshot, text: $"not-matched-{i}");
    Check(Query(store, snapshot, cursor: cursor).Rows.Select(row => row.Path).SequenceEqual(expected), "Cache eviction changed a cursor result.");
});

Test("Expiry uses injected clock and rejects at the exact deadline", () =>
{
    var now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    var store = new Store(() => now);
    var snapshot = Capture(store, lifetime: 30);
    var cursor = Query(store, snapshot).NextCursor;
    now = now.AddSeconds(29);
    Query(store, snapshot, cursor: cursor);
    now = now.AddSeconds(1);
    Error("IndexExpired", () => Query(store, snapshot, cursor: cursor));
    Error("IndexNotFound", () => store.Get(snapshot.Id));
});

Test("New capture removes expired snapshots and evicts the oldest of three", () =>
{
    var now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    var store = new Store(() => now);
    var expired = Capture(store, lifetime: 30);
    now = now.AddSeconds(31);
    var first = Capture(store);
    Error("IndexNotFound", () => store.Get(expired.Id));
    now = now.AddSeconds(1);
    var second = Capture(store);
    now = now.AddSeconds(1);
    var third = Capture(store);
    Error("IndexNotFound", () => store.Get(first.Id));
    Check(store.Get(second.Id) == second && store.Get(third.Id) == third, "Eviction removed the wrong snapshot.");
});

Test("Invalidation blocks existing snapshots while a later capture is usable", () =>
{
    var store = new Store();
    var first = Capture(store);
    var second = Capture(store);
    var cursor = Query(store, first).NextCursor;
    store.Invalidate("change.apply");
    Error("IndexInvalidated", () => Query(store, first, cursor: cursor));
    Error("IndexInvalidated", () => store.Get(second.Id));
    var fresh = Capture(store);
    Check(Query(store, fresh).Total == 3, "Fresh capture incorrectly inherited invalidation.");
});

Test("Release is idempotent and old identifiers cannot be queried", () =>
{
    var store = new Store();
    var snapshot = Capture(store);
    Check(store.Release(snapshot.Id) && !store.Release(snapshot.Id), "Release result is not idempotent.");
    Check(!store.Release(null!) && !store.Release("unknown"), "Unknown release should report false.");
    Error("IndexNotFound", () => Query(store, snapshot));
});

Test("Page sizes, selector lengths and lifetimes obey boundaries", () =>
{
    var store = new Store();
    var snapshot = Capture(store);
    foreach (var limit in new[] { -1, 0, 201, int.MaxValue }) Error("InvalidRequest", () => Query(store, snapshot, max: limit));
    Query(store, snapshot, max: 1);
    Query(store, snapshot, max: 200);
    Query(store, snapshot, text: new string('a', 256), kind: new string('a', 80), plc: new string('a', 4096), scope: new string('a', 4096));
    Error("InvalidRequest", () => Query(store, snapshot, text: new string('a', 257)));
    Error("InvalidRequest", () => Query(store, snapshot, kind: new string('a', 81)));
    Error("InvalidRequest", () => Query(store, snapshot, plc: new string('a', 4097)));
    Error("InvalidRequest", () => Query(store, snapshot, scope: new string('a', 4097)));
    Error("InvalidRequest", () => Capture(store, lifetime: 29));
    Error("InvalidRequest", () => Capture(store, lifetime: 901));
    Capture(store, lifetime: 900);
});

Test("Row payload capacity counts UTF-8 bytes rather than characters", () =>
{
    var store = new Store();
    // Valid JSON containing exactly 65,536 UTF-8 bytes: brackets/quotes + 3-byte CJK characters.
    var exact = "[\"" + new string('温', 21844) + "\"]";
    Check(Encoding.UTF8.GetByteCount(exact) == 65536, "Test fixture byte boundary invalid.");
    var snapshot = Capture(store, [Row("/exact", payload:exact)]);
    Check(Query(store, snapshot).PayloadBytes == 65536, "Payload byte count is not UTF-8.");
    Error("IndexCapacity", () => Capture(store, [Row("/oversized", payload:exact + " ")]));
});

Test("Total snapshot capacity includes identity metadata, and failed capture remains atomic", () =>
{
    var store = new Store();
    var prior = Capture(store);
    var hugeIdentity = "/" + new string('界', (int)(Store.MaximumBytes / 3));
    Error("IndexCapacity", () => Capture(store, [Row(hugeIdentity, payload:"{}") ]));
    Check(store.Get(prior.Id) == prior, "Capacity failure evicted a prior snapshot.");
    var payload = "[\"" + new string('x', 65532) + "\"]";
    Error("IndexCapacity", () => Capture(store, Enumerable.Range(0, 512).Select(i => Row($"/{i}", payload:payload))));
});

Test("Row-count capacity rejects 50,001 entries without losing the previous snapshot", () =>
{
    var store = new Store();
    var prior = Capture(store);
    Error("IndexCapacity", () => Capture(store, Enumerable.Range(0, 50001).Select(i => Row($"/{i:D5}", payload:"{}"))));
    Check(store.Get(prior.Id) == prior, "Row-count failure removed prior snapshot.");
});

Test("512 KiB page payload limit maintains coherent traversal", () =>
{
    var store = new Store();
    var payload = "[\"" + new string('x', 65532) + "\"]";
    var snapshot = Capture(store, Enumerable.Range(0, 19).Select(i => Row($"/{i:D2}", payload:payload)));
    var seen = new List<string>();
    var counts = new List<int>();
    string? cursor = null;
    do
    {
        var page = Query(store, snapshot, max:200, cursor:cursor);
        Check(page.PayloadBytes == page.Rows.Sum(row => Encoding.UTF8.GetByteCount(row.Json)) && page.PayloadBytes <= 512 * 1024,
            "Page payload byte accounting exceeds its limit.");
        Check(page.Offset == seen.Count, "Payload-limited offset skipped rows.");
        seen.AddRange(page.Rows.Select(row => row.Path));
        counts.Add(page.Rows.Length);
        cursor = page.NextCursor;
    } while (cursor is not null);
    Check(counts.SequenceEqual([8,8,3]) && seen.Distinct().Count() == 19, "Payload boundary traversal inconsistent.");
});

Test("Empty snapshot remains queryable with a terminal empty page", () =>
{
    var store = new Store();
    var snapshot = Capture(store, []);
    var page = Query(store, snapshot);
    Check(page.Total == 0 && page.Offset == 0 && page.Rows.Length == 0 && page.NextCursor is null && snapshot.PayloadBytes == 0, "Empty snapshot result invalid.");
});

foreach (var count in new[] { 100, 1000, 10000, 50000 })
{
    Test($"Benchmark {count:N0} namespace/Unicode rows with complete paged traversal", () =>
    {
        var source = Enumerable.Range(0, count).Reverse().Select(i =>
        {
            var plc = $"/project/plcs/PLC_{i % 4}";
            var scope = $"{plc}/units/装置_{i % 7}";
            var name = i % 10 == 0 ? "Shared_Überwachung" : $"FB_温度_{i % 997:D3}";
            return Row($"{scope}/groups/deep/nested/blocks/{i:D5}_{name}", name, i % 5 == 0 ? "type" : "block", plc, scope);
        }).ToArray();
        var store = new Store();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise:true);
        var captureTimer = Stopwatch.StartNew();
        var snapshot = Capture(store, source);
        captureTimer.Stop();
        var firstTimer = Stopwatch.StartNew();
        var first = Query(store, snapshot, max:200);
        firstTimer.Stop();
        var seen = new List<string>(count);
        string? cursor = null;
        var pages = 0;
        var maximumPageBytes = 0;
        var traverseTimer = Stopwatch.StartNew();
        do
        {
            var page = Query(store, snapshot, max:200, cursor:cursor);
            Check(page.Total == count && page.Offset == seen.Count && page.Rows.Length is > 0 and <= 200, "Stress pagination metadata inconsistent.");
            Check(page.PayloadBytes <= 512 * 1024, "Stress page exceeded payload cap.");
            seen.AddRange(page.Rows.Select(row => row.Path));
            maximumPageBytes = Math.Max(maximumPageBytes, page.PayloadBytes);
            ++pages;
            cursor = page.NextCursor;
        } while (cursor is not null);
        traverseTimer.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise:true) - allocatedBefore;
        Check(seen.Count == count && seen.Distinct(StringComparer.Ordinal).Count() == count, "Stress traversal missed or repeated identities.");
        Check(seen.SequenceEqual(source.Select(row => row.Path).Order(StringComparer.Ordinal)), "Stress traversal not canonical/ordinal.");
        var filterTimer = Stopwatch.StartNew();
        var filtered = Query(store, snapshot, max:200, text:"Shared_Überwachung", kind:"type", plc:"/project/plcs/PLC_0");
        filterTimer.Stop();
        var expectedCount = source.Count(row => row.Name.Contains("Shared_Überwachung", StringComparison.OrdinalIgnoreCase) && row.Kind == "type" && row.PlcPath == "/project/plcs/PLC_0");
        Check(filtered.Total == expectedCount, "Stress filtered count differs from independent source predicate.");
        benchmarks.Add(new {
            rowCount=count, snapshotPayloadBytes=snapshot.PayloadBytes, captureMilliseconds=captureTimer.Elapsed.TotalMilliseconds,
            firstPageMilliseconds=firstTimer.Elapsed.TotalMilliseconds, fullTraversalMilliseconds=traverseTimer.Elapsed.TotalMilliseconds,
            filteredQueryMilliseconds=filterTimer.Elapsed.TotalMilliseconds, filteredMatches=filtered.Total,
            pages, maximumPagePayloadBytes=maximumPageBytes, managedAllocatedBytesDuringCaptureAndTraversal=allocated,
            processWorkingSetBytes=Environment.WorkingSet, uniqueIdentities=seen.Count
        });
        Console.WriteLine($"  {count:N0} rows: capture {captureTimer.Elapsed.TotalMilliseconds:F2} ms, traversal {traverseTimer.Elapsed.TotalMilliseconds:F2} ms, {pages} pages, {snapshot.PayloadBytes:N0} snapshot bytes");
    });
}

var reportPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("artifacts/project-index-store-tests.json");
var helperPath = Path.GetFullPath("src/TiaMcp.Openness.V21/ProjectIndexStore.cs");
var testAssemblyPath = typeof(Store).Assembly.Location;
string FileHash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
var report = new {
    schemaVersion=1, capturedAtUtc=DateTimeOffset.UtcNow, status=failures.Count == 0 ? "passed" : "failed",
    testsPassed=tests.Count - failures.Count, testsFailed=failures.Count, tests, benchmarks,
    environment=new { framework=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        os=System.Runtime.InteropServices.RuntimeInformation.OSDescription, processArchitecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() },
    implementationEvidence=new { helperSourcePath=File.Exists(helperPath) ? helperPath : null,
        helperSourceSha256=File.Exists(helperPath) ? FileHash(helperPath) : null, testAssemblyPath, testAssemblySha256=FileHash(testAssemblyPath) },
    scope="Pure immutable index store only. Includes capture, cursor, query and memory/payload accounting; no TIA traversal, native worker, MCP serialization, VM, or runtime execution was exercised.",
    benchmarkCaveats=new[] { "Single measurements, sequential sizes; JIT and warm caches affect timings.", "Allocated bytes are process-wide allocations during capture/traversal, not retained cache memory.", "512 KiB cap counts stored row JSON payload, not MCP transport envelope.", "Working set includes the test process and earlier tests; no application memory ceiling is asserted." }
};
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented=true }), new UTF8Encoding(false));
Console.WriteLine($"{tests.Count - failures.Count}/{tests.Count} passed. Report: {reportPath}");
return failures.Count == 0 ? 0 : 1;
