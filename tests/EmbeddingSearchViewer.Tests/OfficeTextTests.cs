using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Data;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public sealed class OfficeTextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "esv_office_" + Guid.NewGuid().ToString("N"));

    public OfficeTextTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeZip(string name, params (string Entry, string Xml)[] entries)
    {
        string path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, xml) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(xml);
        }
        return path;
    }

    private const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
    private const string A = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
    private const string S = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"";

    [Fact]
    public void Word_ParagraphsAreRead_RunsAreJoined()
    {
        string path = MakeZip("a.docx", ("word/document.xml",
            $"<w:document {W}><w:body><w:p><w:r><w:t>Hello </w:t></w:r><w:r><w:t>world</w:t></w:r></w:p><w:p><w:r><w:t>Second</w:t></w:r></w:p><w:p/></w:body></w:document>"));

        var chunks = OfficeTextExtractor.Extract(path);

        var chunk = Assert.Single(chunks);
        Assert.Null(chunk.Number);
        Assert.Equal("Hello world\nSecond", chunk.Text);
    }

    [Fact]
    public void Slides_AreOneChunkEach_InSlideOrder()
    {
        string path = MakeZip("a.pptx",
            ("ppt/slides/slide10.xml", $"<p:sld xmlns:p=\"p\" {A}><a:p><a:r><a:t>Tenth</a:t></a:r></a:p></p:sld>"),
            ("ppt/slides/slide2.xml", $"<p:sld xmlns:p=\"p\" {A}><a:p><a:r><a:t>Second</a:t></a:r></a:p></p:sld>"),
            ("ppt/slides/slide1.xml", $"<p:sld xmlns:p=\"p\" {A}><a:p><a:r><a:t>First</a:t></a:r></a:p><a:p><a:r><a:t>Body</a:t></a:r></a:p></p:sld>"));

        var chunks = OfficeTextExtractor.Extract(path);

        Assert.Equal(new[] { "First\nBody", "Second", "Tenth" }, chunks.Select(c => c.Text));
        Assert.Equal(new int?[] { 1, 2, 3 }, chunks.Select(c => c.Number));
    }

    [Fact]
    public void Sheets_TextCellsAreRead_NumbersAreLeftOut()
    {
        string path = MakeZip("a.xlsx",
            ("xl/workbook.xml", $"<workbook {S}><sheets><sheet name=\"Budget\" sheetId=\"1\"/></sheets></workbook>"),
            ("xl/sharedStrings.xml", $"<sst {S}><si><t>Item</t></si><si><r><t>Total </t></r><r><t>cost</t></r></si></sst>"),
            ("xl/worksheets/sheet1.xml",
                $"<worksheet {S}><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>" +
                "<row r=\"2\"><c r=\"A2\"><v>42</v></c><c r=\"B2\" t=\"inlineStr\"><is><t>inline</t></is></c></row></sheetData></worksheet>"));

        var chunk = Assert.Single(OfficeTextExtractor.Extract(path));

        Assert.Equal(1, chunk.Number);
        Assert.Equal("[Budget]\nItem\tTotal cost\ninline", chunk.Text);
    }

    [Fact]
    public void LongText_IsSplitIntoChunksOfLimitedSize()
    {
        string paragraphs = string.Concat(Enumerable.Range(0, 20).Select(i => $"<w:p><w:r><w:t>{new string('x', 400)}</w:t></w:r></w:p>"));
        string path = MakeZip("long.docx", ("word/document.xml", $"<w:document {W}><w:body>{paragraphs}</w:body></w:document>"));

        var chunks = OfficeTextExtractor.Extract(path);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= OfficeTextExtractor.MaxChunkChars));
    }

    [Fact]
    public void NotAZipFile_IsReportedAsInvalidData()
    {
        string path = Path.Combine(_root, "old.docx");
        File.WriteAllBytes(path, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 1, 2, 3 });

        Assert.Throws<InvalidDataException>(() => OfficeTextExtractor.Extract(path));
    }

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult(new float[truncateDim]);

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float[]>>(items.Select(_ => new float[truncateDim]).ToList());
    }

    [Fact]
    public async Task Ingestor_StoresTextChunks_AndFallsBackToTheNameForUnreadableFiles()
    {
        var ingestor = new OfficeMediaIngestor(new StubEmbeddingService());
        string good = MakeZip("deck.pptx",
            ("ppt/slides/slide1.xml", $"<p:sld xmlns:p=\"p\" {A}><a:p><a:r><a:t>Quarterly plan</a:t></a:r></a:p></p:sld>"),
            ("ppt/slides/slide2.xml", $"<p:sld xmlns:p=\"p\" {A}><a:p><a:r><a:t>Risks</a:t></a:r></a:p></p:sld>"));
        string bad = Path.Combine(_root, "old.pptx");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3 });

        var (asset, chunks) = await ingestor.IngestAsync(good);
        Assert.Equal(MediaType.Document, asset.MediaType);
        Assert.Equal(2, asset.DurationOrPages);
        Assert.Equal(new int?[] { 1, 2 }, chunks.Select(c => c.PageNumber));

        var (_, fallback) = await ingestor.IngestAsync(bad);
        var only = Assert.Single(fallback);
        Assert.Equal("old.pptx", only.SnippetText);
    }
}
