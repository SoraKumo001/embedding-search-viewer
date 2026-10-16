using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbeddingSearchViewer.Core.Localization;
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
    private string _downloadButtonText = Loc.T("ModelSetup.Button.Start");

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
            StatusText = Loc.T("ModelSetup.Status.Available");
            DetailText = string.Empty;
            ProgressPercent = 100;
        }
        else if (remaining < total)
        {
            StatusText = Loc.T("ModelSetup.Status.CanResume", FormatBytes(remaining));
            ProgressPercent = 100.0 * (total - remaining) / total;
            DownloadButtonText = Loc.T("ModelSetup.Button.Resume");
        }
        else
        {
            StatusText = Loc.T("ModelSetup.Status.WillDownload", TotalSizeText);
            ProgressPercent = 0;
            DownloadButtonText = Loc.T("ModelSetup.Button.Start");
        }
    }

    [RelayCommand]
    public async Task DownloadAsync()
    {
        if (IsDownloading) return;

        _cts = new CancellationTokenSource();
        IsDownloading = true;
        ErrorMessage = null;
        StatusText = Loc.T("ModelSetup.Status.Preparing");

        var progress = new Progress<ModelDownloadProgress>(OnProgress);
        try
        {
            await _downloader.DownloadAsync(_files, _directory, progress, _cts.Token);

            StatusText = Loc.T("ModelSetup.Status.Loading");
            IsVerifying = false;
            await _host.ReloadAsync();

            if (_host.IsAvailable)
            {
                IsDownloading = false;
                IsInstalled = true;
                ProgressPercent = 100;
                StatusText = Loc.T("ModelSetup.Status.Done");
                DetailText = string.Empty;
            }
            else
            {
                IsDownloading = false;
                ErrorMessage = _host.UnavailableReason;
                StatusText = Loc.T("ModelSetup.Status.LoadFailed");
            }
        }
        catch (OperationCanceledException)
        {
            IsDownloading = false;
            StatusText = Loc.T("ModelSetup.Status.Stopped");
            DownloadButtonText = Loc.T("ModelSetup.Button.Resume");
            Refresh();
        }
        catch (ModelDownloadException ex)
        {
            IsDownloading = false;
            ErrorMessage = ex.Message;
            StatusText = Loc.T("ModelSetup.Status.Failed");
            DownloadButtonText = Loc.T("ModelSetup.Button.Retry");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            IsDownloading = false;
            ErrorMessage = Loc.T("ModelSetup.WriteFailed", ex.Message);
            StatusText = Loc.T("ModelSetup.Status.Failed");
            DownloadButtonText = Loc.T("ModelSetup.Button.Retry");
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
            ModelDownloadPhase.Verifying => Loc.T("ModelSetup.Status.Verifying", p.FileIndex, p.FileCount),
            _ => Loc.T("ModelSetup.Status.Downloading", p.FileIndex, p.FileCount, p.CurrentFile)
        };

        string speed = p.BytesPerSecond > 0 ? Loc.T("ModelSetup.Speed", FormatBytes((long)p.BytesPerSecond)) : string.Empty;
        string eta = p.BytesPerSecond > 0 && p.Phase == ModelDownloadPhase.Downloading
            ? Loc.T("ModelSetup.Eta", FormatDuration(TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / p.BytesPerSecond)))
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
        ? Loc.T("ModelSetup.Duration.HoursMinutes", (int)t.TotalHours, t.Minutes)
        : t.TotalMinutes >= 1 ? Loc.T("ModelSetup.Duration.MinutesSeconds", (int)t.TotalMinutes, t.Seconds) : Loc.T("ModelSetup.Duration.Seconds", Math.Max(1, (int)t.TotalSeconds));
}
