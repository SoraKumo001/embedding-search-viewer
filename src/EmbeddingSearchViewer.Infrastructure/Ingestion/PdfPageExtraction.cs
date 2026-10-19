using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Infrastructure.Onnx;
using SkiaSharp;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <param name="PageNumber">1-based page number.</param>
/// <param name="ImagePath">PNG of the rendered page.</param>
/// <param name="Text">Text layer of the page (empty for scans without OCR).</param>
public sealed record PdfPageContent(int PageNumber, string ImagePath, string Text);

public sealed record PdfExtraction(int TotalPages, IReadOnlyList<PdfPageContent> Pages);

/// <summary>Turns a PDF into one image and the text of each page.</summary>
public interface IPdfPageExtractor
{
    /// <exception cref="InvalidDataException">The file is not a readable PDF (corrupt or password protected).</exception>
    Task<PdfExtraction> ExtractAsync(string pdfPath, string outputDirectory, CancellationToken cancellationToken = default);
}

/// <summary>
/// Renders pages with the Windows PDF renderer (<c>Windows.Data.Pdf</c>, no extra native files) and reads the text
/// layer with PdfPig. Pages are rendered at about 150 DPI, the same as the former Python sidecar.
/// </summary>
public sealed class WindowsPdfPageExtractor : IPdfPageExtractor
{
    private const double RenderDpi = 150.0;
    private const double DipPerInch = 96.0;     // PdfPage.Size is expressed in 1/96 inch
    private const double MaxLongSidePixels = 3000;

    public async Task<PdfExtraction> ExtractAsync(string pdfPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        PdfDocument document;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
            document = await PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or FileNotFoundException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"The PDF could not be opened: {ex.Message}", ex);
        }

        if (document.IsPasswordProtected)
        {
            throw new InvalidDataException("The PDF is password protected.");
        }

        using var textReader = OpenTextReader(pdfPath);
        var pages = new List<PdfPageContent>((int)document.PageCount);

        // The renderer multiplies the requested size by the display scaling of the machine (125% on many PCs), so the
        // factor is measured on the first page and compensated for, which keeps the output at 150 DPI everywhere.
        double correction = 1.0;

        for (uint index = 0; index < document.PageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string imagePath = Path.Combine(outputDirectory, $"page_{index + 1:D4}.png");
            var (actual, requested) = await RenderPageAsync(document, index, imagePath, correction);
            if (index == 0 && requested > 0 && Math.Abs((double)actual / requested - 1.0) > 0.02)
            {
                correction = (double)requested / actual;
                await RenderPageAsync(document, index, imagePath, correction);
            }
            pages.Add(new PdfPageContent((int)index + 1, imagePath, ReadText(textReader, (int)index + 1)));
        }

        return new PdfExtraction((int)document.PageCount, pages);
    }

    /// <summary>Renders a page to a PNG and returns the width of the written image and the width that was requested.</summary>
    private static async Task<(int ActualWidth, int RequestedWidth)> RenderPageAsync(PdfDocument document, uint index, string imagePath, double correction)
    {
        using var page = document.GetPage(index);

        double scale = RenderDpi / DipPerInch;
        double width = page.Size.Width * scale, height = page.Size.Height * scale;
        double longSide = Math.Max(width, height);
        if (longSide > MaxLongSidePixels)
        {
            width *= MaxLongSidePixels / longSide;
            height *= MaxLongSidePixels / longSide;
        }

        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(width * correction)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(height * correction)),
        };

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);

        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        await File.WriteAllBytesAsync(imagePath, bytes);

        int actualWidth;
        // NativeDecodeGate is a leaf: synchronous Skia only, no await, no other lock taken after it.
        lock (NativeDecodeGate.Lock)
        {
            using var codec = SKCodec.Create(new MemoryStream(bytes));
            actualWidth = codec?.Info.Width ?? 0;
        }
        return (actualWidth, (int)Math.Max(1, Math.Round(width)));
    }

    /// <summary>Opens the text layer reader; null when PdfPig cannot parse the file (pages are then indexed by image only).</summary>
    private static PigDocument? OpenTextReader(string pdfPath)
    {
        try
        {
            return PigDocument.Open(pdfPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string ReadText(PigDocument? reader, int pageNumber)
    {
        if (reader == null) return string.Empty;
        try
        {
            return reader.GetPage(pageNumber).Text.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }
}
