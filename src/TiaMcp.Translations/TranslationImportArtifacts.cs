using System.Text;
using System.Text.RegularExpressions;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

internal sealed class TranslationImportArtifacts(string root)
{
    internal EngineeringArtifactReference Retain<T>(T value) => RetainBytes(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(Node(value))));
    internal EngineeringArtifactReference RetainBytes(byte[] bytes)
    {
        Require(bytes is { Length: > 0 and <= TranslationRetention.WorkbookLimit }, "TranslationArtifactLimit", "Nonempty retained import artifacts are bounded to 100 MiB.");
        bytes = bytes.ToArray();
        var reference = new EngineeringArtifactReference("translation-import/blobs/" + Sha256(bytes), Sha256(bytes));
        string path = PathFor(reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            try { File.Move(pending, path, false); }
            catch (IOException) when (File.Exists(path)) { File.Delete(pending); }
        }
        _ = ReadBytes(reference);
        return reference;
    }
    internal byte[] ReadBytes(EngineeringArtifactReference reference)
    {
        var bytes = TranslationRetention.ReadBounded(PathFor(reference), TranslationRetention.WorkbookLimit);
        Require(Sha256(bytes) == reference.Sha256, "TranslationIntegrityFailure", "Retained native artifact changed.");
        return bytes;
    }
    internal T Read<T>(EngineeringArtifactReference reference) => Parse<T>(Encoding.UTF8.GetString(ReadBytes(reference)));
    private string PathFor(EngineeringArtifactReference reference)
    {
        Require(reference is not null && Regex.IsMatch(reference.RelativePath ?? "", "^translation-import/blobs/[a-f0-9]{64}$") &&
            reference.RelativePath!.EndsWith("/" + reference.Sha256, StringComparison.Ordinal), "InvalidTranslationReference", "Use an exact retained import artifact.");
        string path = Path.Combine(root, reference.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        TranslationRetention.NoLinks(path); return path;
    }
}
