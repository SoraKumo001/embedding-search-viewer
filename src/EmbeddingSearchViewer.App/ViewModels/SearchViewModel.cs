using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Core.Services;
using EmbeddingSearchViewer.Infrastructure.Library;

namespace EmbeddingSearchViewer_App.ViewModels;

public partial class SearchViewModel : ObservableObject
{
    private readonly HybridSearchEngine _searchEngine;
    private readonly SearchHistoryStore _historyStore;

    private readonly AppSettingsStore? _appSettings;
    private bool _applyingHistory;

    /// <summary>Pinned searches first, then the recent ones.</summary>
    public IReadOnlyList<SearchHistoryEntry> History => _historyStore.Load();

    /// <summary>Removes the recent searches; the pinned ones stay unless <paramref name="includePinned"/> is set.</summary>
    public void ClearHistory(bool includePinned = false) => _historyStore.Clear(includePinned);

    public void RemoveHistory(Guid id) => _historyStore.Remove(id);

    /// <summary>Pins or unpins an entry; false when the pin limit is reached.</summary>
    public bool SetHistoryPinned(Guid id, bool pinned) => _historyStore.SetPinned(id, pinned);

    /// <summary>Whether the conditions of a search (type, similarity, sort, filters) are kept in its history entry.</summary>
    [ObservableProperty]
    private bool _saveOptionsInHistory = true;

    partial void OnSaveOptionsInHistoryChanged(bool value)
    {
        if (_appSettings == null) return;
        var settings = _appSettings.Load();
        settings.SaveSearchOptionsInHistory = value;
        _appSettings.Save(settings);
    }

    public const int MaxSuggestions = 8;

    /// <summary>Past query texts to offer while typing: those that start with the input first, then those that contain it.</summary>
    public IReadOnlyList<string> SuggestHistory(string? input)
    {
        string typed = (input ?? string.Empty).Trim();
        if (typed.Length == 0) return Array.Empty<string>();

        var texts = History
            .Select(e => e.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t, typed, StringComparison.CurrentCultureIgnoreCase))
            .Select(t => t!)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return texts.Where(t => t.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase))
            .Concat(texts.Where(t => !t.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase)
                                     && t.Contains(typed, StringComparison.CurrentCultureIgnoreCase)))
            .Take(MaxSuggestions)
            .ToList();
    }

    /// <summary>
    /// Puts a history entry back into the search form (text, image and, if saved, the conditions) and runs it.
    /// An image that has been moved or deleted is reported instead of searching with a different query.
    /// </summary>
    public async Task ApplyHistoryAsync(SearchHistoryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ImagePath) && !System.IO.File.Exists(entry.ImagePath))
        {
            ErrorMessage = Loc.T("Search.History.ImageMissing", entry.ImagePath);
            return;
        }

        _applyingHistory = true;
        try
        {
            QueryText = entry.Text ?? string.Empty;
            QueryImagePath = string.IsNullOrWhiteSpace(entry.ImagePath) ? null : entry.ImagePath;

            var o = entry.Options;
            if (o != null)
            {
                SelectedMediaTypeIndex = o.MediaTypeIndex;
                MinSimilarity = o.MinSimilarity;
                SortIndex = o.SortIndex;
                EnableFts = o.EnableFts;
                GroupVideos = o.GroupVideos;
                FilterDateFrom = o.DateFrom;
                FilterDateTo = o.DateTo;
                FilterMinSizeMb = o.MinSizeMb ?? double.NaN;
                FilterMaxSizeMb = o.MaxSizeMb ?? double.NaN;
                FilterFolder = o.Folder ?? string.Empty;
            }
        }
        finally
        {
            _applyingHistory = false;
        }
        await SearchCommand.ExecuteAsync(null);
    }

    private SearchHistoryOptions SnapshotOptions() => new()
    {
        MediaTypeIndex = SelectedMediaTypeIndex,
        MinSimilarity = MinSimilarity,
        SortIndex = SortIndex,
        EnableFts = EnableFts,
        GroupVideos = GroupVideos,
        DateFrom = FilterDateFrom,
        DateTo = FilterDateTo,
        MinSizeMb = double.IsNaN(FilterMinSizeMb) ? null : FilterMinSizeMb,
        MaxSizeMb = double.IsNaN(FilterMaxSizeMb) ? null : FilterMaxSizeMb,
        Folder = string.IsNullOrWhiteSpace(FilterFolder) ? null : FilterFolder.Trim()
    };

    [ObservableProperty]
    private string _queryText = string.Empty;

    [ObservableProperty]
    private string? _queryImagePath;

    [ObservableProperty]
    private int _selectedMediaTypeIndex = 0; // 0: All, 1: Pdf, 2: Image, 3: Video

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinSimilarityText))]
    private float _minSimilarity = 0.0f;

    public string MinSimilarityText => MinSimilarity <= 0f ? Loc.T("Search.MinSimilarity.None") : Loc.T("Search.MinSimilarity.AtLeast", MinSimilarity);

    // Metadata filters. Dates are local calendar days; sizes are in MB (NaN = not set, as NumberBox reports for an empty box).
    [ObservableProperty]
    private DateTimeOffset? _filterDateFrom;

    [ObservableProperty]
    private DateTimeOffset? _filterDateTo;

    [ObservableProperty]
    private double _filterMinSizeMb = double.NaN;

    [ObservableProperty]
    private double _filterMaxSizeMb = double.NaN;

    [ObservableProperty]
    private string _filterFolder = string.Empty;

    [RelayCommand]
    public void ClearFilters()
    {
        FilterDateFrom = null;
        FilterDateTo = null;
        FilterMinSizeMb = double.NaN;
        FilterMaxSizeMb = double.NaN;
        FilterFolder = string.Empty;
    }

    private static long? MbToBytes(double mb) => double.IsNaN(mb) || mb < 0 ? null : (long)(mb * 1024 * 1024);

    [ObservableProperty]
    private int _sortIndex; // 0: relevance, 1: similarity, 2: file name

    private List<SearchResultItem> _rawResults = new();

    partial void OnSortIndexChanged(int value) => ApplySort();

    private void ApplySort()
    {
        var previous = SelectedResult;
        IEnumerable<SearchResultItem> ordered = SortIndex switch
        {
            1 => _rawResults.OrderByDescending(r => r.SimilarityScore),
            2 => _rawResults.OrderBy(r => r.Asset.FileName, StringComparer.CurrentCultureIgnoreCase),
            _ => _rawResults.OrderByDescending(r => r.CombinedScore)
        };

        SearchResults.Clear();
        foreach (var item in ordered)
        {
            SearchResults.Add(item);
        }

        if (previous != null && SearchResults.Contains(previous))
        {
            SelectedResult = previous;
        }
    }

    /// <summary>Shows a video once in the results (its best scene) instead of one row per matching scene.</summary>
    [ObservableProperty]
    private bool _groupVideos = true;

    partial void OnGroupVideosChanged(bool value)
    {
        // Show the same search in the other form right away.
        bool hasQuery = !string.IsNullOrWhiteSpace(QueryText) || !string.IsNullOrWhiteSpace(QueryImagePath);
        if (!_applyingHistory && hasQuery && SearchResults.Count > 0 && !IsSearching) _ = SearchCommand.ExecuteAsync(null);
    }

    // ---- scenes of the selected video (below the preview) ----

    public ObservableCollection<SearchResultItem> Scenes { get; } = new();

    /// <summary>True while a video is selected: the scene list and the search inside the video are shown.</summary>
    [ObservableProperty]
    private bool _isVideoSelected;

    [ObservableProperty]
    private string _scenesHeaderText = string.Empty;

    /// <summary>A text to look for inside the selected video only.</summary>
    [ObservableProperty]
    private string _sceneQueryText = string.Empty;

    [ObservableProperty]
    private int _sceneSortIndex; // 0: best match first, 1: in the order of the video

    [ObservableProperty]
    private SearchResultItem? _selectedScene;

    [ObservableProperty]
    private bool _isSceneSearching;

    [ObservableProperty]
    private string? _sceneErrorMessage;

    private List<SearchResultItem> _sceneSource = new();
    private string _sceneSearchText = string.Empty; // empty: the scenes that matched the main search

    partial void OnSelectedResultChanged(SearchResultItem? value) => LoadScenesFor(value);

    partial void OnSceneSortIndexChanged(int value) => ApplyScenes(SelectedScene);

    /// <summary>Lists the scenes of a video result: the ones that matched the main search.</summary>
    private void LoadScenesFor(SearchResultItem? result)
    {
        SceneQueryText = string.Empty;
        SceneErrorMessage = null;
        _sceneSearchText = string.Empty;

        if (result == null || result.Asset.MediaType != MediaType.Video)
        {
            IsVideoSelected = false;
            _sceneSource = new List<SearchResultItem>();
            ApplyScenes(null);
            return;
        }

        IsVideoSelected = true;
        _sceneSource = (result.Scenes.Count > 0
                ? result.Scenes
                : _rawResults.Where(r => r.Asset.Id == result.Asset.Id))
            .ToList();
        if (_sceneSource.Count == 0) _sceneSource.Add(result);
        ApplyScenes(_sceneSource.FirstOrDefault(s => s.Chunk.Id == result.Chunk.Id));
    }

    private void ApplyScenes(SearchResultItem? select)
    {
        IEnumerable<SearchResultItem> ordered = SceneSortIndex == 1
            ? _sceneSource.OrderBy(s => s.Chunk.StartTimeMs ?? 0)
            : _sceneSource.OrderByDescending(s => s.CombinedScore);

        Scenes.Clear();
        foreach (var scene in ordered) Scenes.Add(scene);
        SelectedScene = select != null && Scenes.Contains(select) ? select : null;

        ScenesHeaderText = !IsVideoSelected
            ? string.Empty
            : _sceneSearchText.Length == 0
                ? Loc.T("Search.Scenes.Header", Scenes.Count)
                : Loc.T("Search.Scenes.HeaderInVideo", _sceneSearchText, Scenes.Count);
    }

    /// <summary>Looks for scenes inside the selected video only; an empty text goes back to the scenes of the main search.</summary>
    [RelayCommand]
    public async Task SearchInVideoAsync(CancellationToken cancellationToken)
    {
        var video = SelectedResult;
        if (video == null || video.Asset.MediaType != MediaType.Video) return;

        string text = SceneQueryText.Trim();
        if (text.Length == 0)
        {
            ResetScenes();
            return;
        }

        IsSceneSearching = true;
        SceneErrorMessage = null;
        try
        {
            var query = new SearchQuery
            {
                QueryText = text,
                MediaTypeFilter = MediaType.Video,
                AssetIdFilter = video.Asset.Id,
                MinSimilarity = MinSimilarity,
                TruncateDim = TruncateDim,
                EnableFts = EnableFts,
                TopK = 200
            };

            var results = await _searchEngine.SearchAsync(query, cancellationToken);
            if (SelectedResult?.Asset.Id != video.Asset.Id) return; // another video was selected meanwhile

            _sceneSource = results.ToList();
            _sceneSearchText = text;
            ApplyScenes(null);
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            SceneErrorMessage = Loc.T("Search.Scenes.Error", ex.Message);
        }
        finally
        {
            IsSceneSearching = false;
        }
    }

    /// <summary>Back to the scenes that matched the main search.</summary>
    [RelayCommand]
    public void ResetScenes() => LoadScenesFor(SelectedResult);

    [ObservableProperty]
    private int _truncateDim = 768;

    [ObservableProperty]
    private bool _enableFts = true;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private SearchResultItem? _selectedResult;

    private static string InitialEmptyText => Loc.T("Search.Empty.Initial");

    [ObservableProperty]
    private string _emptyStateText = InitialEmptyText;

    public ObservableCollection<SearchResultItem> SearchResults { get; } = new();

    [ObservableProperty]
    private string _resultsHeaderText = Loc.T("Search.ResultsHeader", 0);

    public SearchViewModel(HybridSearchEngine searchEngine, SearchHistoryStore historyStore, AppSettingsStore? appSettings = null)
    {
        _searchEngine = searchEngine;
        _historyStore = historyStore;
        _appSettings = appSettings;
        if (appSettings != null) _saveOptionsInHistory = appSettings.Load().SaveSearchOptionsInHistory;
    }

    /// <summary>Clears the query and the results of the previous profile; they do not belong to the new index.</summary>
    public void ResetForProfileChange()
    {
        QueryText = string.Empty;
        QueryImagePath = null;
        _rawResults = new List<SearchResultItem>();
        SearchResults.Clear();
        SelectedResult = null;
        ErrorMessage = null;
        ResultsHeaderText = Loc.T("Search.ResultsHeader", 0);
        EmptyStateText = InitialEmptyText;
        OnPropertyChanged(nameof(History));
    }

    [RelayCommand]
    public async Task SearchAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(QueryText) && string.IsNullOrWhiteSpace(QueryImagePath))
        {
            return;
        }

        IsSearching = true;
        ErrorMessage = null;

        try
        {
            MediaType? filter = SelectedMediaTypeIndex switch
            {
                1 => MediaType.Pdf,
                2 => MediaType.Image,
                3 => MediaType.Video,
                4 => MediaType.Document,
                _ => null
            };

            var query = new SearchQuery
            {
                QueryText = QueryText.Trim(),
                QueryImagePath = QueryImagePath,
                MediaTypeFilter = filter,
                MinSimilarity = MinSimilarity,
                TruncateDim = TruncateDim,
                EnableFts = EnableFts,
                ModifiedFromUtc = FilterDateFrom?.LocalDateTime.Date.ToUniversalTime(),
                ModifiedToUtc = FilterDateTo?.LocalDateTime.Date.AddDays(1).ToUniversalTime(),
                MinSizeBytes = MbToBytes(FilterMinSizeMb),
                MaxSizeBytes = MbToBytes(FilterMaxSizeMb),
                FolderContains = string.IsNullOrWhiteSpace(FilterFolder) ? null : FilterFolder.Trim(),
                GroupVideoSegments = GroupVideos,
                TopK = 50
            };

            var results = await _searchEngine.SearchAsync(query, cancellationToken);

            if (!string.IsNullOrWhiteSpace(QueryText) || !string.IsNullOrWhiteSpace(QueryImagePath))
            {
                _historyStore.Add(new SearchHistoryEntry
                {
                    Text = QueryText,
                    ImagePath = string.IsNullOrWhiteSpace(QueryImagePath) ? null : QueryImagePath,
                    Options = SaveOptionsInHistory ? SnapshotOptions() : null
                });
            }

            _rawResults = results.ToList();
            SearchResults.Clear();
            ApplySort();

            ResultsHeaderText = Loc.T("Search.ResultsHeader", SearchResults.Count);
            EmptyStateText = SearchResults.Count == 0 ? Loc.T("Search.Empty.NoMatch") : string.Empty;

            if (SearchResults.Count > 0)
            {
                SelectedResult = SearchResults[0];
            }
            else
            {
                SelectedResult = null;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.T("Search.Error", ex.Message);
            if (SearchResults.Count == 0) EmptyStateText = string.Empty;
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    public void SetQueryImage(string imagePath)
    {
        QueryImagePath = imagePath;
    }

    [RelayCommand]
    public void ClearQueryImage()
    {
        QueryImagePath = null;
    }

    [RelayCommand]
    public void Clear()
    {
        QueryText = string.Empty;
        QueryImagePath = null;
        _rawResults = new List<SearchResultItem>();
        SearchResults.Clear();
        SelectedResult = null;
        ErrorMessage = null;
        ResultsHeaderText = Loc.T("Search.ResultsHeader", 0);
        EmptyStateText = InitialEmptyText;
    }
}
