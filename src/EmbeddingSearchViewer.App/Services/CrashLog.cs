using System;
using System.IO;
using System.Threading.Tasks;

namespace EmbeddingSearchViewer_App.Services;

/// <summary>
/// Writes unexpected errors to <c>%LOCALAPPDATA%\EmbeddingSearchViewer\logs\error.log</c>, so that a crash leaves a trace
/// (Windows records nothing for some of them). The file is started again when it gets large.
/// </summary>
public static class CrashLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Lock = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmbeddingSearchViewer", "logs", "error.log");

    /// <summary>Starts recording errors that nothing else catches (any thread, and tasks nobody waited for).</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.IsTerminating ? "Unhandled (the app is ending)" : "Unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    public static void Write(string where, Exception? ex)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes) File.Delete(FilePath);

                File.AppendAllText(FilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where} (memory {Environment.WorkingSet / (1024 * 1024)} MB)\n{ex}\n\n");
            }
        }
        catch (Exception)
        {
            // Writing the log must never be what fails.
        }
    }
}
