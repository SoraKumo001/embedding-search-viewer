using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace EmbeddingSearchViewer_App.Services;

/// <summary>Shows a file in File Explorer with the file itself selected.</summary>
public static class ExplorerLauncher
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint count, IntPtr[]? pidls, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pointer);

    /// <summary>
    /// Opens Explorer with <paramref name="path"/> selected. When the file is gone, its folder is opened instead.
    /// Returns false when neither exists or Explorer could not be started.
    /// </summary>
    public static bool Reveal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string full;
        try { full = Path.GetFullPath(path.Replace('/', '\\')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        try
        {
            if (File.Exists(full) || Directory.Exists(full))
            {
                // The shell API takes the path as is: no quoting or comma problems like the /select, command line has.
                if (SelectWithShell(full)) return true;
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
                return true;
            }

            if (Path.GetDirectoryName(full) is { } folder && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Debug.WriteLine($"Could not open Explorer: {ex.Message}");
        }
        return false;
    }

    private static bool SelectWithShell(string fullPath)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(fullPath, IntPtr.Zero, out pidl, 0, out _) < 0 || pidl == IntPtr.Zero) return false;
            // With no child items, the item named by the first argument is the one that gets selected.
            return SHOpenFolderAndSelectItems(pidl, 0, null, 0) >= 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return false;
        }
        finally
        {
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }
}
