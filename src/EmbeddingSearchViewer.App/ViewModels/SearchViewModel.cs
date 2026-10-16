using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Core.Services;
using EmbeddingSearchViewer.Infrastructure.Library;

namespace EmbeddingSearchViewer_App.ViewModels;

public partial class SearchViewModel : ObservableObject
{
    private readonly HybridSearchEngine _searchEngine;
    private readonly SearchHistoryStore _historyStore;

    public IReadOnlyList<string> History => _historyStore.Load();

    public void ClearHistory() => _historyStore.Clear();

    [ObservableProperty]
    private string _queryText = string.Empty;

    [ObservableProperty]
    private string? _queryImagePath;

    [ObservableProperty]
    private int _selectedMediaTypeIndex = 0; // 0: All, 1: Pdf, 2: Image, 3: Video

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinSimilarityText))]
    private float _minSimilarity = 0.0f;

    public string MinSimilarityText => MinSimilarity <= 0f ? "制限なし" : $"{MinSimilarity:P0} 以上";

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

    private const string InitialEmptyText = "検索語を入力するか、画像を指定して検索してください";

    [ObservableProperty]
    private string _emptyStateText = InitialEmptyText;

    public ObservableCollection<SearchResultItem> SearchResults { get; } = new();

    [ObservableProperty]
    private string _resultsHeaderText = "検索結果 (0 件)";

    public SearchViewModel(HybridSearchEngine searchEngine, SearchHistoryStore historyStore)
    {
        _searchEngine = searchEngine;
        _historyStore = historyStore;
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
                TopK = 50
            };

            var results = await _searchEngine.SearchAsync(query, cancellationToken);

            if (!string.IsNullOrWhiteSpace(QueryText) && string.IsNullOrWhiteSpace(QueryImagePath))
            {
                _historyStore.Add(QueryText);
            }

            _rawResults = results.ToList();
            SearchResults.Clear();
            ApplySort();

            ResultsHeaderText = $"検索結果 ({SearchResults.Count} 件)";
            EmptyStateText = SearchResults.Count == 0 ? "一致する結果がありません。条件を変えて検索してください" : string.Empty;

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
            ErrorMessage = $"検索中にエラーが発生しました: {ex.Message}";
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
        ResultsHeaderText = "検索結果 (0 件)";
        EmptyStateText = InitialEmptyText;
    }
}
