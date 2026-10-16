using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EmbeddingSearchViewer.Infrastructure.Library;

/// <summary>Where the main window was, in screen pixels. The bounds are those of the normal (not maximized) window.</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool IsMaximized);

/// <summary>A screen's usable area (the work area, without the taskbar), in screen pixels.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// Decides whether a saved window placement can be used again. The screens may have changed since it was saved
/// (a monitor unplugged, another resolution, another scaling), and a window that cannot be reached is worse than one at
/// the default place, so anything doubtful is rejected and the caller falls back to the default.
/// </summary>
public static class WindowPlacementValidator
{
    public const int MinWidth = 640;
    public const int MinHeight = 400;

    /// <summary>At least this much of the title bar must lie on a screen, so that the window can be grabbed and moved.</summary>
    public const int MinVisibleTitleWidth = 160;
    public const int TitleBarHeight = 32;

    /// <summary>Returns the placement when it is usable on the given screens; null means "use the default".</summary>
    public static WindowPlacement? Validate(WindowPlacement? saved, IReadOnlyList<ScreenRect> screens)
    {
        if (saved == null || screens.Count == 0) return null;
        if (saved.Width < MinWidth || saved.Height < MinHeight) return null;

        // Larger than all the screens together (plus the few pixels a window may stick out): a leftover of another setup.
        int left = screens.Min(s => s.X), top = screens.Min(s => s.Y);
        int right = screens.Max(s => s.Right), bottom = screens.Max(s => s.Bottom);
        const int Slack = 64;
        if (saved.Width > right - left + Slack || saved.Height > bottom - top + Slack) return null;

        // The strip of the title bar that is on a screen must be big enough to grab.
        long titleX1 = saved.X, titleX2 = (long)saved.X + saved.Width;
        long titleY1 = saved.Y, titleY2 = (long)saved.Y + TitleBarHeight;
        bool reachable = screens.Any(s =>
        {
            long w = Math.Min(titleX2, s.Right) - Math.Max(titleX1, s.X);
            long h = Math.Min(titleY2, s.Bottom) - Math.Max(titleY1, s.Y);
            return w >= Math.Min(MinVisibleTitleWidth, saved.Width) && h >= TitleBarHeight - 8;
        });
        return reachable ? saved : null;
    }
}

/// <summary>Keeps the main window's placement in a small JSON file under LocalAppData (the same for every profile).</summary>
public sealed class WindowPlacementStore
{
    private readonly string _path;

    public WindowPlacementStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmbeddingSearchViewer",
            "window.json");
    }

    /// <summary>The saved placement, or null when there is none or the file is unreadable.</summary>
    public WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(_path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(WindowPlacement placement)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(placement, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The window position is a convenience; failing to save it must not get in the way of closing the app.
        }
    }
}
