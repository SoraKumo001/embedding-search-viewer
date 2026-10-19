using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer.Infrastructure.Onnx;
using EmbeddingSearchViewer.Infrastructure.Profiles;
using EmbeddingSearchViewer_App.Services;

namespace EmbeddingSearchViewer_App.ViewModels;

/// <summary>Where a backup should go; the file is chosen in the view before the command runs.</summary>
/// <param name="DestinationPath">The zip to write.</param>
/// <param name="IncludeCache">True to put the profile's cache (thumbnails, converted results) into the archive too.</param>
/// <param name="Progress">Receives the stages of the backup for the progress dialog.</param>
public sealed record ProfileBackupRequest(
    string DestinationPath,
    bool IncludeCache,
    IProgress<ProfileBackupProgress>? Progress = null);

/// <summary>Which archive to restore; the file is chosen in the view before the command runs.</summary>
public sealed record ProfileRestoreRequest(string ZipPath);

/// <summary>Lists, switches, creates, renames and deletes profiles, backs them up and restores them, and refreshes the screens that depend on the current one.</summary>
public partial class ProfileViewModel : ObservableObject
{
    private readonly ProfileManager _profiles;
    private readonly IngestionPipelineWorker _worker;
    private readonly FolderWatcher _watcher;
    private readonly LibraryViewModel _library;
    private readonly SearchViewModel _search;
    private readonly ProfileBackupService _backup;
    private readonly ProfileRestoreService _restore;

    public ObservableCollection<ProfileInfo> Profiles { get; } = new();

    [ObservableProperty]
    private ProfileInfo _current;

    /// <summary>Non-null after an operation was refused or failed; the view shows it to the user.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>The model the app indexes with today; a backup made with another one restores with a warning.</summary>
    private static readonly string CurrentModelTag =
        EmbeddingModelLocation.ModelTag(EmbeddingModelLocation.DefaultPrecision);

    public ProfileViewModel(
        ProfileManager profiles,
        IngestionPipelineWorker worker,
        FolderWatcher watcher,
        LibraryViewModel library,
        SearchViewModel search)
    {
        _profiles = profiles;
        _worker = worker;
        _watcher = watcher;
        _library = library;
        _search = search;

        // The backup and restore services are built here from the profile list they work on, so the composition root
        // does not have to know about them. The backup reads the busy state at the start of every run, and refuses
        // while ingestion is writing into a profile.
        _backup = new ProfileBackupService(profiles, CurrentBusyState);
        _restore = new ProfileRestoreService(profiles);

        _current = profiles.Current;
        Reload();

        // The commands are only offered while the app is idle, the same rule that guards a profile switch.
        _library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.IsIndexing)) NotifyProfileCommandsCanExecuteChanged();
        };
    }

    private void Reload()
    {
        Profiles.Clear();
        foreach (var profile in _profiles.Profiles) Profiles.Add(profile);
        Current = _profiles.Current;
    }

    private static string IndexingMessage => Loc.T("Profile.IndexingBlocked");

    /// <summary>The background work that must be quiet before a backup or a restore may touch a profile's files.</summary>
    private BackupBusyState CurrentBusyState() =>
        new(_worker.IsRunning, _library.IsIndexing, _library.IsClearing);

    /// <summary>True when a backup or a restore may start; the same condition that blocks a profile switch.</summary>
    public bool CanRunProfileOperation => !_worker.IsRunning && !_library.IsIndexing;

    private void NotifyProfileCommandsCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanRunProfileOperation));
        BackupCurrentCommand.NotifyCanExecuteChanged();
        BackupAllCommand.NotifyCanExecuteChanged();
        RestoreSingleCommand.NotifyCanExecuteChanged();
        RestoreAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Clears the previous error and, when indexing is under way, refuses with the message it was given.</summary>
    private bool EnsureNotIndexing(string blockedMessage)
    {
        ErrorMessage = null;
        if (CanRunProfileOperation) return true;
        ErrorMessage = blockedMessage;
        return false;
    }

    /// <summary>Switches to the profile and reloads the library and search screens. Returns false when refused.</summary>
    public async Task<bool> SwitchAsync(string id)
    {
        ErrorMessage = null;
        if (id == _profiles.Current.Id) return true;

        if (!CanRunProfileOperation)
        {
            ErrorMessage = IndexingMessage;
            Reload();
            return false;
        }

        try
        {
            _profiles.Switch(id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            Reload();
            return false;
        }

        _watcher.Refresh(discardPending: true);
        _search.ResetForProfileChange();
        await _library.ReloadForProfileChangeAsync();
        Reload();
        return true;
    }

    /// <summary>Creates an empty profile and switches to it.</summary>
    public async Task<bool> CreateAsync(string name)
    {
        ErrorMessage = null;
        if (!CanRunProfileOperation)
        {
            ErrorMessage = IndexingMessage;
            return false;
        }

        try
        {
            var profile = _profiles.Create(name);
            Reload();
            return await SwitchAsync(profile.Id);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            return false;
        }
    }

    public bool RenameCurrent(string name)
    {
        ErrorMessage = null;
        try
        {
            _profiles.Rename(_profiles.Current.Id, name);
            Reload();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>Deletes a profile other than the current one, with its index and cache.</summary>
    public bool Delete(string id)
    {
        ErrorMessage = null;
        try
        {
            _profiles.Delete(id);
            Reload();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            return false;
        }
    }

    public bool CanDelete => _profiles.Profiles.Count > 1;

    public ProfileInfo? Find(string id) => Profiles.FirstOrDefault(p => p.Id == id);

    // ---- backup and restore ----

    /// <summary>Writes the current profile into the chosen zip. A failure leaves <see cref="ErrorMessage"/> set.</summary>
    [RelayCommand(CanExecute = nameof(CanRunProfileOperation))]
    private async Task BackupCurrentAsync(ProfileBackupRequest request)
    {
        if (!EnsureNotIndexing(Loc.T("Backup.Busy"))) return;

        try
        {
            await _backup.BackupSingleAsync(
                _profiles.Current.Id, request.DestinationPath, request.IncludeCache, request.Progress);
        }
        catch (Exception ex)
        {
            // The service reports its own reasons (a busy database, a failed verification); they are shown wrapped.
            CrashLog.Write("BackupCurrent", ex);
            ErrorMessage = Loc.T("Backup.Failed", ex.Message);
        }
    }

    /// <summary>Writes every profile into the chosen zip. A failure leaves <see cref="ErrorMessage"/> set.</summary>
    [RelayCommand(CanExecute = nameof(CanRunProfileOperation))]
    private async Task BackupAllAsync(ProfileBackupRequest request)
    {
        if (!EnsureNotIndexing(Loc.T("Backup.Busy"))) return;

        try
        {
            await _backup.BackupAllAsync(request.DestinationPath, request.IncludeCache, request.Progress);
        }
        catch (Exception ex)
        {
            CrashLog.Write("BackupAll", ex);
            ErrorMessage = Loc.T("Backup.Failed", ex.Message);
        }
    }

    /// <summary>
    /// Reads the manifest of the chosen backup without touching a profile folder, for the confirmation dialog.
    /// Returns null and sets <see cref="ErrorMessage"/> when the archive cannot be read.
    /// </summary>
    public async Task<RestorePreview?> PreviewRestoreAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        try
        {
            return await _restore.PreviewAsync(zipPath, cancellationToken);
        }
        catch (Exception ex)
        {
            CrashLog.Write("PreviewRestore", ex);
            ErrorMessage = Loc.T("Restore.PreviewFailed", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reads and validates every manifest of the chosen all-profiles backup without touching a profile folder, for the
    /// confirmation dialog. Returns null and sets <see cref="ErrorMessage"/> when the archive cannot be read.
    /// </summary>
    public async Task<AllRestorePreview?> PreviewRestoreAllAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        try
        {
            return await _restore.PreviewAllAsync(zipPath, cancellationToken);
        }
        catch (Exception ex)
        {
            CrashLog.Write("PreviewRestoreAll", ex);
            ErrorMessage = Loc.T("Restore.PreviewFailed", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Restores the chosen backup over the profile it names, then shows the restored data on screen.
    /// A failure leaves <see cref="ErrorMessage"/> set.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunProfileOperation))]
    private async Task RestoreSingleAsync(ProfileRestoreRequest request)
    {
        if (!EnsureNotIndexing(Loc.T("Restore.IndexingBlocked"))) return;

        try
        {
            // The manifest is read again here (it is small) so the reload knows which profile was replaced.
            var preview = await _restore.PreviewAsync(request.ZipPath);
            await _restore.RestoreSingleAsync(request.ZipPath, CurrentBusyState);
            await ReloadAfterRestoreAsync(preview.ProfileId);
        }
        catch (Exception ex)
        {
            CrashLog.Write("RestoreSingle", ex);
            ErrorMessage = Loc.T("Restore.Failed", ex.Message);
        }
    }

    /// <summary>
    /// Restores the chosen all-profiles backup over the whole profile tree, then shows the restored data on screen.
    /// A failure leaves <see cref="ErrorMessage"/> set.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunProfileOperation))]
    private async Task RestoreAllAsync(ProfileRestoreRequest request)
    {
        if (!EnsureNotIndexing(Loc.T("Restore.IndexingBlocked"))) return;

        try
        {
            await _restore.RestoreAllAsync(request.ZipPath, CurrentBusyState);
            await ReloadAfterRestoreAllAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write("RestoreAll", ex);
            ErrorMessage = Loc.T("Restore.Failed", ex.Message);
        }
    }

    /// <summary>
    /// Brings every screen back in line with a profile whose data was just replaced. The restored profile is normally
    /// the one on screen; when it is another one, switching to it is what shows the restored data.
    /// </summary>
    private async Task ReloadAfterRestoreAsync(string restoredProfileId)
    {
        if (!string.Equals(restoredProfileId, _profiles.Current.Id, StringComparison.Ordinal))
        {
            await SwitchAsync(restoredProfileId);
            return;
        }

        _watcher.Refresh(discardPending: true);
        _search.ResetForProfileChange();
        await _library.ReloadForProfileChangeAsync();
        Reload();
    }

    /// <summary>
    /// Brings every screen back in line after an all-profiles restore replaced every profile's data. Unlike the single
    /// form there is no profile to switch to: the library, search and watcher are pointed at the restored data, and the
    /// restored profile list is read from profiles.json on the next start.
    /// </summary>
    private async Task ReloadAfterRestoreAllAsync()
    {
        _watcher.Refresh(discardPending: true);
        _search.ResetForProfileChange();
        await _library.ReloadForProfileChangeAsync();
        Reload();
    }
}
