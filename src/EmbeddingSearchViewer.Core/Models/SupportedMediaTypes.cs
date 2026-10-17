using System;
using System.Collections.Generic;
using System.IO;

namespace EmbeddingSearchViewer.Core.Models;

/// <summary>Which kinds of files the user wants in the index.</summary>
public readonly record struct MediaTypeSelection(bool Images, bool Pdfs, bool Videos, bool Documents)
{
    // new() on a struct ignores parameter defaults and would select nothing, hence a named value.
    public static MediaTypeSelection All => new(true, true, true, true);
}

public static class SupportedMediaTypes
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif", ".ico", ".tif", ".tiff"
    };

    public static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    /// <summary>Office files: only their text is read (no Office installation needed). The old binary formats (.doc/.xls/.ppt) are not supported.</summary>
    public static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx"
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv",
        ".m4v", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".3gp", ".ogv"
    };

    public static bool IsSupported(string path)
    {
        string ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext) || PdfExtensions.Contains(ext) || VideoExtensions.Contains(ext) || OfficeExtensions.Contains(ext);
    }

    /// <summary>True when the file is a supported type and the user has chosen to index that type.</summary>
    public static bool IsSelected(string path, MediaTypeSelection selection)
    {
        return ResolveMediaType(path) switch
        {
            MediaType.Image => selection.Images,
            MediaType.Pdf => selection.Pdfs,
            MediaType.Video => selection.Videos,
            MediaType.Document => selection.Documents,
            _ => false
        };
    }

    public static MediaType ResolveMediaType(string path)
    {
        string ext = Path.GetExtension(path);
        if (ImageExtensions.Contains(ext)) return MediaType.Image;
        if (PdfExtensions.Contains(ext)) return MediaType.Pdf;
        if (VideoExtensions.Contains(ext)) return MediaType.Video;
        if (OfficeExtensions.Contains(ext)) return MediaType.Document;
        return MediaType.Other;
    }
}
