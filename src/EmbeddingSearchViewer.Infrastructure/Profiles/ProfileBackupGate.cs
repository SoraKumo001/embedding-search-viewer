using System;
using EmbeddingSearchViewer.Core.Localization;

namespace EmbeddingSearchViewer.Infrastructure.Profiles;

/// <summary>
/// The background work that must be quiet before a backup (or, later, a restore) may touch a profile's files. It only
/// carries the states a caller can observe today; PR2/PR3 fill in the remaining ones (<c>LibraryClearing</c> and
/// <c>WatcherPending</c>) without changing this shape.
/// </summary>
/// <param name="IngestionRunning">IngestionPipelineWorker.IsRunning.</param>
/// <param name="LibraryIndexing">LibraryViewModel.IsIndexing.</param>
/// <param name="LibraryClearing">
/// PR2 extension point: LibraryViewModel.IsClearing. It is a separate flag from <paramref name="LibraryIndexing"/> so a
/// clear that outlives an ingest still blocks a backup. Always false in PR1.
/// </param>
/// <param name="WatcherPending">
/// PR3 extension point: the FolderWatcher has debounced events waiting to be flushed. Always false in PR1.
/// </param>
public readonly record struct BackupBusyState(
    bool IngestionRunning,
    bool LibraryIndexing,
    bool LibraryClearing = false,
    bool WatcherPending = false)
{
    /// <summary>True when any of the tracked background activities is still running.</summary>
    public bool IsBusy => IngestionRunning || LibraryIndexing || LibraryClearing || WatcherPending;

    /// <summary>The two states available in PR1.</summary>
    public static BackupBusyState From(bool ingestionRunning, bool libraryIndexing)
        => new(ingestionRunning, libraryIndexing);
}

/// <summary>
/// A pre-flight gate that refuses a backup while the app is still writing to a profile. It is deliberately static and
/// dependency-free: the UI layer owns the worker and view models, so it passes their current state in, and this type
/// only decides whether that state is idle enough.
/// <para>
/// PR2/PR3 will add the counterpart to <see cref="ThrowIfBusy(BackupBusyState)"/>: a <c>Suspend</c>/<c>Resume</c> pair
/// that pauses the folder watcher and queues the ingestion worker, waits for the in-flight run to drain, and only then
/// lets the operation proceed. The gate stays the single place that answers "may we start now?".
/// </para>
/// </summary>
public static class ProfileBackupGate
{
    /// <summary>Throws when the app is not idle enough to start a backup/restore.</summary>
    public static void ThrowIfBusy(BackupBusyState state)
    {
        if (state.IsBusy) throw new InvalidOperationException(Loc.T("Backup.Busy"));
    }

    /// <summary>Convenience overload for the two states PR1 can observe.</summary>
    public static void ThrowIfBusy(bool ingestionRunning, bool libraryIndexing)
        => ThrowIfBusy(BackupBusyState.From(ingestionRunning, libraryIndexing));

    // TODO(PR2): public static IDisposable Suspend(BackupBusyState current, ...) — stop the folder watcher, queue the
    //            worker, await the current run, and expose the previous state back through Resume. Until it exists,
    //            callers call ThrowIfBusy and surface the refusal.
}
