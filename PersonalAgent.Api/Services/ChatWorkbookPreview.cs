using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PersonalAgent.Api.Services;

internal static class ChatWorkbookPreview
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Link = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Package = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static string? Read(string Name, byte[] Content)
    {
        if (!Name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            && !Name.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var Stream = new MemoryStream(Content, writable: false);
            using var Archive = new ZipArchive(Stream, ZipArchiveMode.Read);
            var Workbook = ReadPart(Archive, "xl/workbook.xml")?.Root;
            var Relationships = ReadPart(Archive, "xl/_rels/workbook.xml.rels")?.Root;
            if (Workbook is null || Relationships is null) return null;
            var Strings = ReadPart(Archive, "xl/sharedStrings.xml")?.Root?.Elements(Sheet + "si")
                .Select(CellText).ToList() ?? [];
            var Preview = new StringBuilder("Workbook preview (up to 6 sheets, 30 populated rows and 20 cells per row):");
            foreach (var Tab in Workbook.Descendants(Sheet + "sheet").Take(6))
            {
                var Relationship = Relationships.Elements(Package + "Relationship")
                    .FirstOrDefault(Item => (string?)Item.Attribute("Id") == (string?)Tab.Attribute(Link + "id"));
                var Target = (string?)Relationship?.Attribute("Target");
                if (string.IsNullOrWhiteSpace(Target) || (string?)Relationship?.Attribute("TargetMode") == "External") continue;
                var Part = Target.StartsWith('/') ? Target.TrimStart('/') : "xl/" + Target;
                Preview.Append("\nSheet: ").Append((string?)Tab.Attribute("name"));
                var Worksheet = ReadPart(Archive, Part)?.Root;
                if (Worksheet is null)
                {
                    Preview.Append("\n[Worksheet could not be previewed.]");
                    continue;
                }
                foreach (var Row in Worksheet.Descendants(Sheet + "row").Where(Row => Row.HasElements).Take(30))
                {
                    var Values = Row.Elements(Sheet + "c").Where(Cell => Cell.HasElements).Take(20)
                        .Select(Cell => $"{(string?)Cell.Attribute("r")}: {Value(Cell, Strings)}");
                    Preview.Append('\n').AppendJoin(" | ", Values);
                    if (Preview.Length > 12_000)
                        return Preview.ToString(0, 12_000) + "\n[Workbook preview truncated.]";
                }
            }
            return Preview.ToString();
        }
        catch (Exception Error) when (Error is InvalidDataException or XmlException or IOException)
        {
            return null;
        }
    }

    private static XDocument? ReadPart(ZipArchive Archive, string Path)
    {
        const int PartLimit = 4 * 1024 * 1024;
        if (Archive.GetEntry(Path) is not { Length: <= PartLimit } Entry) return null;
        using var Stream = Entry.Open();
        using var Xml = XmlReader.Create(Stream, new XmlReaderSettings { MaxCharactersInDocument = PartLimit });
        return XDocument.Load(Xml);
    }

    private static string CellText(XElement Element) => string.Concat(Element.Descendants(Sheet + "t").Select(Value => Value.Value));

    private static string Value(XElement Cell, IReadOnlyList<string> Strings)
    {
        var Raw = (string?)Cell.Element(Sheet + "v") ?? "";
        var Value = (string?)Cell.Attribute("t") switch
        {
            "inlineStr" => CellText(Cell),
            "s" => int.TryParse(Raw, out var Id) && Id >= 0 && Id < Strings.Count ? Strings[Id] : "[shared text unavailable]",
            "b" => Raw == "1" ? "TRUE" : "FALSE",
            _ => Raw
        };
        return Value.Length > 240 ? Value[..240] + "â€¦" : Value;
    }
}
