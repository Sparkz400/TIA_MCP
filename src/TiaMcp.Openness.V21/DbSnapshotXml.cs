#nullable disable
using System.Linq;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    // Each InterfaceSnapshot export has a new DocumentInfo/Created, so two exports hold the same stored snapshot exactly
    // when their SW.Blocks.InterfaceSnapshot elements are equal.
    internal static class DbSnapshotXml
    {
        internal static bool SameSnapshot(string expectedPath, string actualPath)
        {
            var expected = Snapshot(expectedPath);
            return expected != null && XNode.DeepEquals(expected, Snapshot(actualPath));
        }

        private static XElement Snapshot(string path) =>
            XDocument.Load(path).Root?.Elements().SingleOrDefault(e => e.Name.LocalName == "SW.Blocks.InterfaceSnapshot");
    }
}
