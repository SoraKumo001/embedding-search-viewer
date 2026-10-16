using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer.Infrastructure.Profiles;

namespace EmbeddingSearchViewer_App.ViewModels;

/// <summary>Lists, switches, creates, renames and deletes profiles, and refreshes the screens that depend on the current one.</summary>
public partial class ProfileViewModel : ObservableObject
{
    private readonly ProfileManager _profiles;
    private readonly IngestionPipelineWorker _worker;
    private readonly FolderWatcher _watcher;
    private readonly LibraryViewModel _library;
    private readonly SearchViewModel _search;

    public ObservableCollection<ProfileInfo> Profiles { get; } = new();

    [ObservableProperty]
    private ProfileInfo _current;

    /// <summary>Non-null after an operation was refused or failed; the view shows it to the user.</summary>
    [ObservableProperty]
    private string? _errorMessage;

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
        _current = profiles.Current;
        Reload();
    }

    private void Reload()
    {
        Profiles.Clear();
        foreach (var profile in _profiles.Profiles) Profiles.Add(profile);
        Current = _profiles.Current;
    }

    private static string IndexingMessage => Loc.T("Profile.IndexingBlocked");

    /// <summary>Switches to the profile and reloads the library and search screens. Returns false when refused.</summary>
    public async Task<bool> SwitchAsync(string id)
    {
        ErrorMessage = null;
        if (id == _profiles.Current.Id) return true;

        if (_worker.IsRunning || _library.IsIndexing)
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
        if (_worker.IsRunning || _library.IsIndexing)
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
}
