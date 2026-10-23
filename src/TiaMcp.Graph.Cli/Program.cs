using System.Text;
using System.Text.Json;
using TiaMcp.Graph;

namespace TiaMcp.Graph.Cli;

public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Length == 1 && args[0] is "--help" or "-h") { Usage(output); return 0; }
            if (!(args.Length == 3 && args[0] is "inspect" or "view" or "preserve") && !(args.Length == 4 && args[0] == "compare"))
            { Usage(error); return 2; }
            var before = GraphDocument.Load(args[1]);
            GraphDocument? after = args[0] == "compare" ? GraphDocument.Load(args[2]) : null;
            string destination = args[^1];
            byte[] result = args[0] switch
            {
                "inspect" => Encoding.UTF8.GetBytes(before.ToJson()),
                "view" => Encoding.UTF8.GetBytes(before.ToHtml()),
                "compare" => Encoding.UTF8.GetBytes(before.CompareTo(after!).ToJson()),
                _ => before.GetOriginalBytes()
            };
            // CreateNew also protects input aliases/hard links and previously generated evidence from replacement.
            using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(result);
            output.WriteLine(Path.GetFullPath(destination));
            bool invalid = before.Inspection.HasErrors || after?.Inspection.HasErrors == true;
            output.WriteLine(invalid ? "Report written with structural errors. This input is not certified for native import or runtime behavior."
                : "Offline report written. This input is not certified for native import or runtime behavior.");
            return invalid ? 3 : 0;
        }
        catch (Exception ex) when (ex is GraphException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error.WriteLine(JsonSerializer.Serialize(new { code = ex is GraphException g ? g.Code : "GraphCommandFailed", message = ex.Message }));
            return 1;
        }
    }

    private static void Usage(TextWriter writer) => writer.WriteLine("""
        Offline GRAPH document tools (.NET 10). Outputs must not already exist.
          inspect  input.xml report.json
          view     input.xml sequence.html
          compare  before.xml after.xml changes.json
          preserve input.xml original-copy.xml
        Exit codes: 0 report written, 1 input/output failure, 2 usage, 3 report written with structural errors.
        Unknown content is retained and reported. Recorded native qualification applies only to its exact fixture profiles.
        """);
}
