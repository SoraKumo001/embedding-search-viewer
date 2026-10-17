using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <summary>A piece of an Office file's text. <see cref="Number"/> is the slide or sheet number (null for Word, which has no fixed pages).</summary>
public sealed record OfficeTextChunk(int? Number, string Text);

/// <summary>
/// Reads the text of .docx / .pptx / .xlsx files. These are zip files of XML, so no Office installation and no extra
/// library is needed. The files are read as streams, and the amount of text taken is capped, so a huge sheet cannot use up memory.
/// </summary>
public static class OfficeTextExtractor
{
    public const int MaxChunkChars = 1500;
    public const int MaxChunks = 200;

    private const long MaxEntryBytes = 256L * 1024 * 1024;
    private const int MaxTotalChars = MaxChunkChars * MaxChunks;

    private static readonly XmlReaderSettings ReaderSettings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    /// <exception cref="InvalidDataException">Not an OOXML file (an old .doc/.xls/.ppt, an encrypted or a damaged file).</exception>
    public static IReadOnlyList<OfficeTextChunk> Extract(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".docx" => ExtractWord(zip),
                ".pptx" => ExtractSlides(zip),
                ".xlsx" => ExtractSheets(zip),
                _ => throw new InvalidDataException($"Not an Office file: {path}")
            };
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or NotSupportedException)
        {
            throw new InvalidDataException($"Unsupported or corrupt Office file: {path}", ex);
        }
    }

    private static IReadOnlyList<OfficeTextChunk> ExtractWord(ZipArchive zip)
    {
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("word/document.xml is missing.");
        return Pack(null, ReadParagraphs(entry)).Select(t => new OfficeTextChunk(null, t)).ToList();
    }

    private static IReadOnlyList<OfficeTextChunk> ExtractSlides(ZipArchive zip)
    {
        var slides = zip.Entries
            .Select(e => (Entry: e, Match: Regex.Match(e.FullName, @"^ppt/slides/slide(\d+)\.xml$")))
            .Where(x => x.Match.Success)
            .OrderBy(x => int.Parse(x.Match.Groups[1].Value))
            .ToList();
        if (slides.Count == 0) throw new InvalidDataException("The file has no slides.");

        var result = new List<OfficeTextChunk>();
        int number = 0;
        foreach (var (entry, _) in slides)
        {
            number++;
            foreach (string text in Pack(null, ReadParagraphs(entry))) result.Add(new OfficeTextChunk(number, text));
            if (result.Count >= MaxChunks) break;
        }
        return result.Take(MaxChunks).ToList();
    }

    private static IReadOnlyList<OfficeTextChunk> ExtractSheets(ZipArchive zip)
    {
        var sheets = zip.Entries
            .Select(e => (Entry: e, Match: Regex.Match(e.FullName, @"^xl/worksheets/sheet(\d+)\.xml$")))
            .Where(x => x.Match.Success)
            .OrderBy(x => int.Parse(x.Match.Groups[1].Value))
            .ToList();
        if (sheets.Count == 0) throw new InvalidDataException("The file has no sheets.");

        var shared = ReadSharedStrings(zip);
        var names = ReadSheetNames(zip);

        var result = new List<OfficeTextChunk>();
        int number = 0;
        foreach (var (entry, _) in sheets)
        {
            number++;
            string? name = number <= names.Count ? names[number - 1] : null;
            foreach (string text in Pack(name == null ? null : $"[{name}]", ReadRows(entry, shared))) result.Add(new OfficeTextChunk(number, text));
            if (result.Count >= MaxChunks) break;
        }
        return result.Take(MaxChunks).ToList();
    }

    private static XmlReader Open(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes) throw new InvalidDataException($"{entry.FullName} is too large.");
        return XmlReader.Create(entry.Open(), ReaderSettings);
    }

    /// <summary>The text of every paragraph (w:p in Word, a:p in PowerPoint); runs, tabs and line breaks are joined.</summary>
    private static IEnumerable<string> ReadParagraphs(ZipArchiveEntry entry)
    {
        using var reader = Open(entry);
        var sb = new StringBuilder();
        long total = 0;
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "t":
                        sb.Append(reader.ReadElementContentAsString());
                        continue;
                    case "tab":
                        sb.Append('\t');
                        break;
                    case "br":
                    case "cr":
                        sb.Append(' ');
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p")
            {
                string paragraph = sb.ToString().Trim();
                sb.Clear();
                if (paragraph.Length > 0)
                {
                    total += paragraph.Length;
                    yield return paragraph;
                    if (total >= MaxTotalChars) yield break;
                }
            }
            reader.Read();
        }
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return list;

        using var reader = Open(entry);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si") continue;
            list.Add(ReadTexts(reader.ReadSubtree()));
        }
        return list;
    }

    private static List<string> ReadSheetNames(ZipArchive zip)
    {
        var names = new List<string>();
        var entry = zip.GetEntry("xl/workbook.xml");
        if (entry == null) return names;

        using var reader = Open(entry);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet")
            {
                names.Add(reader.GetAttribute("name") ?? string.Empty);
            }
        }
        return names;
    }

    /// <summary>The text cells of a sheet, one line per row (cells separated by tabs). Numbers are left out: they say little about what a file is about.</summary>
    private static IEnumerable<string> ReadRows(ZipArchiveEntry entry, List<string> shared)
    {
        using var reader = Open(entry);
        var row = new List<string>();
        long total = 0;
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
            {
                string? type = reader.GetAttribute("t");
                string? value = null;
                string inline = string.Empty;
                using (var cell = reader.ReadSubtree())
                {
                    while (cell.Read())
                    {
                        if (cell.NodeType != XmlNodeType.Element) continue;
                        if (cell.LocalName == "v") value = cell.ReadElementContentAsString();
                        else if (cell.LocalName == "is") inline = ReadTexts(cell.ReadSubtree());
                    }
                }
                reader.Read(); // ReadSubtree leaves the reader on the cell's end

                string? text = type switch
                {
                    "s" when int.TryParse(value, out int index) && index >= 0 && index < shared.Count => shared[index],
                    "inlineStr" => inline,
                    "str" => value,
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(text)) row.Add(text.Trim());
                continue;
            }

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row")
            {
                if (row.Count > 0)
                {
                    string line = string.Join('\t', row);
                    row.Clear();
                    total += line.Length;
                    yield return line;
                    if (total >= MaxTotalChars) yield break;
                }
            }
            reader.Read();
        }
    }

    private static string ReadTexts(XmlReader subtree)
    {
        using (subtree)
        {
            var sb = new StringBuilder();
            while (subtree.Read())
            {
                if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "t") sb.Append(subtree.ReadElementContentAsString());
            }
            return sb.ToString();
        }
    }

    /// <summary>Joins pieces (paragraphs, rows) into chunks of at most <see cref="MaxChunkChars"/> characters; a longer piece is cut.</summary>
    internal static IEnumerable<string> Pack(string? heading, IEnumerable<string> pieces)
    {
        var sb = new StringBuilder();
        if (heading != null) sb.Append(heading);
        int headingLength = sb.Length;

        foreach (string piece in pieces)
        {
            for (int start = 0; start < piece.Length; start += MaxChunkChars)
            {
                string part = piece.Substring(start, Math.Min(MaxChunkChars, piece.Length - start));
                if (sb.Length > headingLength && sb.Length + 1 + part.Length > MaxChunkChars)
                {
                    yield return sb.ToString();
                    sb.Clear();
                    if (heading != null) sb.Append(heading);
                }
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(part);
            }
        }
        if (sb.Length > headingLength) yield return sb.ToString();
    }
}
