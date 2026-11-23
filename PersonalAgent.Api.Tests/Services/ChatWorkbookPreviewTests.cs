using System.IO.Compression;

using FluentAssertions;
using Xunit;

using PersonalAgent.Api.Services;

namespace PersonalAgent.Api.Tests.Services;

public sealed class ChatWorkbookPreviewTests
{
    [Fact]
    public void PreviewReadsNamedSheetsAndSharedStringsWithCachedValues()
    {
        using var Bytes = new MemoryStream();
        using (var Workbook = new ZipArchive(Bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(Workbook, "xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Budget" r:id="rId2" /></sheets>
                </workbook>
                """);
            Write(Workbook, "xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId2" Target="/xl/worksheets/sheet2.xml"/>
                </Relationships>
                """);
            Write(Workbook, "xl/sharedStrings.xml", """
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Travel</t></si></sst>
                """);
            Write(Workbook, "xl/worksheets/sheet2.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData><row r="3"><c r="A3" t="s"><v>0</v></c><c r="C3"><f>50+50</f><v>100</v></c></row></sheetData>
                </worksheet>
                """);
        }
        ChatWorkbookPreview.Read("budget.xlsx", Bytes.ToArray())
            .Should().Contain("Sheet: Budget\nA3: Travel | C3: 100");
        ChatWorkbookPreview.Read("damaged.xlsx", [0x50, 0x4B, 0xFF]).Should().BeNull();
    }

    private static void Write(ZipArchive Workbook, string Path, string Xml)
    {
        using var Writer = new StreamWriter(Workbook.CreateEntry(Path).Open());
        Writer.Write(Xml);
    }
}
