using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace PeerOnQ.DependencyBuilder;

internal static class DeterministicPackage
{
    internal static void Normalize(string path)
    {
        const string corePath = "package/services/metadata/core-properties/peeronq.sipsorcery.psmdcp";
        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        using (var source = ZipFile.OpenRead(path))
        {
            if (source.GetEntry(".signature.p7s") is not null)
                throw new InvalidDataException("A signed package cannot be normalized.");
            foreach (var item in source.Entries)
            {
                using var input = item.Open();
                using var bytes = new MemoryStream(); input.CopyTo(bytes);
                var name = item.FullName;
                var content = bytes.ToArray();
                if (name.EndsWith(".psmdcp", StringComparison.Ordinal)) name = corePath;
                if (name is "_rels/.rels" or "[Content_Types].xml" || name == corePath)
                {
                    using var xmlInput = new MemoryStream(content);
                    var xml = XDocument.Load(xmlInput);
                    foreach (var attribute in xml.Descendants().Attributes().Where(attribute => attribute.Name.LocalName is "Target" or "PartName"))
                        if (attribute.Value.EndsWith(".psmdcp", StringComparison.Ordinal)) attribute.Value = "/" + corePath;
                    if (name == "_rels/.rels")
                    {
                        var relationships = xml.Root!.Elements().OrderBy(element => (string?)element.Attribute("Type"), StringComparer.Ordinal).ToArray();
                        xml.Root.ReplaceNodes(relationships);
                        for (var index = 0; index < relationships.Length; index++) relationships[index].SetAttributeValue("Id", "R" + index);
                    }
                    foreach (var time in xml.Descendants().Where(element => element.Name.LocalName is "created" or "modified"))
                        time.Value = "1980-01-01T00:00:00Z";
                    content = new UTF8Encoding(false).GetBytes(xml.ToString(SaveOptions.DisableFormatting));
                }
                if (!entries.TryAdd(name, content)) throw new InvalidDataException("Package normalization found duplicate parts.");
            }
        }
        var normalized = path + ".normalized";
        using (var output = ZipFile.Open(normalized, ZipArchiveMode.Create))
            foreach (var (name, bytes) in entries)
            {
                var entry = output.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = 0;
                using var stream = entry.Open(); stream.Write(bytes);
            }
        File.Move(normalized, path, overwrite: true);
    }
}
