using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbeddingSearchViewer.Infrastructure.Onnx;

namespace EmbeddingSearchViewer_App.ViewModels;

/// <summary>State and actions of the first-run model download screen.</summary>
public partial class ModelSetupViewModel : ObservableObject
{
    private readonly ModelDownloader _downloader;
    private readonly EmbeddingServiceHost _host;
    private readonly IReadOnlyList<ModelFileInfo> _files;
    private readonly string _directory;
    private CancellationTokenSource? _cts;

    /// <param name="files">Files to download; the manifest of the default precision when null.</param>
    public ModelSetupViewModel(ModelDownloader downloader, EmbeddingServiceHost host, string? directory = null, IReadOnlyList<ModelFileInfo>? files = null)
    {
        _downloader = downloader;
        _host = host;
        _files = files ?? ModelManifest.For(EmbeddingModelLocation.DefaultPrecision);
        _directory = directory ?? EmbeddingModelLocation.DefaultDirectory;
        Refresh();
    }

    public string SourceText => $"{EmbeddingModelLocation.RepoId} (Hugging Face, Apache-2.0)";
    public string SourceUrl => $"https://huggingface.co/{EmbeddingModelLocation.RepoId}";
    public string LocationText => _directory;
    public string TotalSizeText => FormatBytes(_files.Sum(f => f.Size));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadButton))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadButton))]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _isVerifying;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _downloadButtonText = "ダウンロードを開始";

    public bool CanDownload => !IsDownloading && !IsInstalled;
    public bool ShowDownloadButton => !IsInstalled;

    /// <summary>Re-reads what is on disk (call when the screen is shown).</summary>
    public void Refresh()
    {
        if (IsDownloading) return;

        long remaining = ModelDownloader.RemainingBytes(_files, _directory);
        long total = _files.Sum(f => f.Size);
        IsInstalled = _host.IsAvailable || remaining == 0;

        if (IsInstalled)
        {
            StatusText = "モデルは利用可能です。";
            DetailText = string.Empty;
            ProgressPercent = 100;
        }
        else if (remaining < total)
        {
            StatusText = $"前回のダウンロードの続きから再開できます (残り {FormatBytes(remaining)})。";
            ProgressPercent = 100.0 * (total - remaining) / total;
            DownloadButtonText = "ダウンロードを再開";
        }
        else
        {
            StatusText = $"検索に使うAIモデル ({TotalSizeText}) をダウンロードします。";
            ProgressPercent = 0;
            DownloadButtonText = "ダウンロードを開始";
        }
    }

    [RelayCommand]
    public async Task DownloadAsync()
    {
        if (IsDownloading) return;

        _cts = new CancellationTokenSource();
        IsDownloading = true;
        ErrorMessage = null;
        StatusText = "ダウンロードを準備しています...";

        var progress = new Progress<ModelDownloadProgress>(OnProgress);
        try
        {
            await _downloader.DownloadAsync(_files, _directory, progress, _cts.Token);

            StatusText = "モデルを読み込んでいます...";
            IsVerifying = false;
            await _host.ReloadAsync();

            if (_host.IsAvailable)
            {
                IsDownloading = false;
                IsInstalled = true;
                ProgressPercent = 100;
                StatusText = "ダウンロードが完了しました。検索を利用できます。";
                DetailText = string.Empty;
            }
            else
            {
                IsDownloading = false;
                ErrorMessage = _host.UnavailableReason;
                StatusText = "ダウンロードは完了しましたが、モデルを読み込めませんでした。";
            }
        }
        catch (OperationCanceledException)
        {
            IsDownloading = false;
            StatusText = "中断しました。途中までのデータは保存されており、再開できます。";
            DownloadButtonText = "ダウンロードを再開";
            Refresh();
        }
        catch (ModelDownloadException ex)
        {
            IsDownloading = false;
            ErrorMessage = ex.Message;
            StatusText = "ダウンロードに失敗しました。";
            DownloadButtonText = "再試行";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            IsDownloading = false;
            ErrorMessage = $"ファイルを書き込めませんでした: {ex.Message}";
            StatusText = "ダウンロードに失敗しました。";
            DownloadButtonText = "再試行";
        }
        finally
        {
            IsVerifying = false;
            IsDownloading = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    public void Cancel() => _cts?.Cancel();

    private void OnProgress(ModelDownloadProgress p)
    {
        if (!IsDownloading) return; // a late report after the run ended must not overwrite the final state

        ProgressPercent = p.Fraction * 100.0;
        IsVerifying = p.Phase == ModelDownloadPhase.Verifying;

        StatusText = p.Phase switch
        {
            ModelDownloadPhase.Verifying => $"ファイルを検証しています ({p.FileIndex}/{p.FileCount})...",
            _ => $"ダウンロード中 ({p.FileIndex}/{p.FileCount}): {p.CurrentFile}"
        };

        string speed = p.BytesPerSecond > 0 ? $"  {FormatBytes((long)p.BytesPerSecond)}/秒" : string.Empty;
        string eta = p.BytesPerSecond > 0 && p.Phase == ModelDownloadPhase.Downloading
            ? $"  残り約 {FormatDuration(TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / p.BytesPerSecond))}"
            : string.Empty;
        DetailText = $"{FormatBytes(p.BytesDone)} / {FormatBytes(p.BytesTotal)}{speed}{eta}";
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B"
    };

    public static string FormatDuration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}時間{t.Minutes}分"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}分{t.Seconds}秒" : $"{Math.Max(1, (int)t.TotalSeconds)}秒";
}
