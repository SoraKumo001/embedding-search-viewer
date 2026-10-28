using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using Microsoft.Data.Sqlite;
using static EmbeddingSearchViewer.Infrastructure.Data.SqliteHelpers;
using static EmbeddingSearchViewer.Infrastructure.Data.SqliteRetry;

namespace EmbeddingSearchViewer.Infrastructure.Data;

/// <summary>Process-wide SQLitePCLRaw initialisation and sqlite-vec extension loading (shared by every repository).</summary>
internal static class SqliteNative
{
    // --- sqlite-vec native extension state (shared by every repository in the process) ---
    internal static readonly object NativeLock = new();
    internal static readonly object VecLoadLock = new();
    internal static bool _batteriesInitialized;
    internal static int _vecState;                 // 0 = not probed, 1 = available, 2 = unavailable
    internal static volatile string? _vecExtensionPath; // null until a load succeeds

    /// <summary>True once a connection has successfully loaded the vec0 extension.</summary>
    internal static bool VecAvailable => Volatile.Read(ref _vecState) == 1;

    internal static void EnsureNativeInitialized()
    {
        if (Volatile.Read(ref _batteriesInitialized)) return;
        lock (NativeLock)
        {
            if (_batteriesInitialized) return;
            try
            {
                SQLitePCL.Batteries_V2.Init();
            }
            catch
            {
                // Microsoft.Data.Sqlite sets the provider up on first use; a second init is not an error we care about.
            }
            Volatile.Write(ref _batteriesInitialized, true);
        }
    }

    internal static void TryLoadVec(SqliteConnection conn)
    {
        if (Volatile.Read(ref _vecState) == 2) return;

        // Once a candidate path is known, load it on each new connection without taking the global lock.
        var known = _vecExtensionPath;
        if (known != null)
        {
            try
            {
                conn.EnableExtensions(true);
                conn.LoadExtension(known);
            }
            catch
            {
                // The query sites guard on failure and fall back to the brute-force scan.
            }
            return;
        }

        lock (VecLoadLock)
        {
            if (Volatile.Read(ref _vecState) == 2) return;

            known = _vecExtensionPath;
            if (known != null)
            {
                try
                {
                    conn.EnableExtensions(true);
                    conn.LoadExtension(known);
                }
                catch { }
                return;
            }

            try
            {
                conn.EnableExtensions(true);
            }
            catch
            {
                return;
            }

            foreach (var candidate in VecExtensionCandidates())
            {
                try
                {
                    conn.LoadExtension(candidate);
                    _vecExtensionPath = candidate;
                    Volatile.Write(ref _vecState, 1);
                    return;
                }
                catch
                {
                    // Try the next candidate path.
                }
            }

            Volatile.Write(ref _vecState, 2);
        }
    }

    /// <summary>The "vec0" bare name first (the loader search path), then candidate directories the host provides.</summary>
    internal static IEnumerable<string> VecExtensionCandidates()
    {
        yield return "vec0";
        yield return Path.Combine(AppContext.BaseDirectory, "vec0.dll");

        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeDirs)
        {
            foreach (var dir in nativeDirs.Split(Path.PathSeparator))
            {
                var trimmed = dir?.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    yield return Path.Combine(trimmed, "vec0.dll");
                }
            }
        }
    }
}
