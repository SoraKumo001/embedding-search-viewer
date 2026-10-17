using System;
using System.Collections.Generic;
using System.IO;

namespace EmbeddingSearchViewer.Core.Models;

public static class SupportedMediaTypes
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp"
    };

    public static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv"
    };

    public static bool IsSupported(string path)
    {
        string ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext) || PdfExtensions.Contains(ext) || VideoExtensions.Contains(ext);
    }

    public static MediaType ResolveMediaType(string path)
    {
        string ext = Path.GetExtension(path);
        if (ImageExtensions.Contains(ext)) return MediaType.Image;
        if (PdfExtensions.Contains(ext)) return MediaType.Pdf;
        if (VideoExtensions.Contains(ext)) return MediaType.Video;
        return MediaType.Other;
    }
}
