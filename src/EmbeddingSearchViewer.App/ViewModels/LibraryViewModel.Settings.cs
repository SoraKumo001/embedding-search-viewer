using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Core;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer.Infrastructure.Onnx;

namespace EmbeddingSearchViewer_App.ViewModels;

public partial class LibraryViewModel
{
    /// <summary>The settings of the current profile; they are read once per transition and reused when saving.</summary>
    private LibrarySettings? _settings;

    [ObservableProperty]
    private bool _autoIndex;

    /// <summary>The choices for the length of a video segment, in seconds; the first is the standard.</summary>
    public static IReadOnlyList<int> VideoSegmentChoices { get; } = new[] { 15, 30, 60, 120 };

    /// <summary>The same choices as a list, so that a lookup does not build a new list every time.</summary>
    private static readonly List<int> VideoSegmentChoiceList = VideoSegmentChoices.ToList();

    public static IReadOnlyList<string> VideoSegmentLabels { get; } =
        new[] { Loc.T("Library.SegmentLabel.Standard", 15), Loc.T("Library.SegmentLabel", 30), Loc.T("Library.SegmentLabel", 60), Loc.T("Library.SegmentLabel", 120) };

    public IReadOnlyList<string> VideoSegmentOptions => VideoSegmentLabels;

    [ObservableProperty]
    private int _videoSegmentIndex;

    partial void OnVideoSegmentIndexChanged(int value)
    {
        if (!_loadingSettings && value >= 0) SaveSettings();
    }

    [ObservableProperty]
    private bool _indexImages = true;

    [ObservableProperty]
    private bool _indexPdfs = true;

    [ObservableProperty]
    private bool _indexVideos = true;

    [ObservableProperty]
    private bool _indexDocuments = true;

    partial void OnIndexImagesChanged(bool value) { if (!_loadingSettings) SaveSettings(); }
    partial void OnIndexPdfsChanged(bool value) { if (!_loadingSettings) SaveSettings(); }
    partial void OnIndexVideosChanged(bool value) { if (!_loadingSettings) SaveSettings(); }
    partial void OnIndexDocumentsChanged(bool value) { if (!_loadingSettings) SaveSettings(); }

    [ObservableProperty]
    private bool _videoAutoSegment = true;

    partial void OnVideoAutoSegmentChanged(bool value)
    {
        if (!_loadingSettings) SaveSettings();
    }

    /// <summary>When on, videos are decoded with FFmpeg first (the Windows media stack as the fallback); off uses the Windows media stack alone.</summary>
    [ObservableProperty]
    private bool _useFfmpegDecoder = true;

    partial void OnUseFfmpegDecoderChanged(bool value)
    {
        if (!_loadingSettings) SaveSettings();
    }

    /// <summary>
    /// When on, FFmpeg runs in a child process (one per video) instead of inside the app; off keeps it in the app. A
    /// build without the child executable keeps it in the app even when this is on. Applies to videos indexed from now on.
    /// </summary>
    [ObservableProperty]
    private bool _useFfmpegWorker = true;

    partial void OnUseFfmpegWorkerChanged(bool value)
    {
        if (!_loadingSettings) SaveSettings();
    }

    partial void OnAutoIndexChanged(bool value)
    {
        if (!_loadingSettings) SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            // The settings of this profile were already read: they are not read again for every change.
            var settings = _settings ??= _store.Load();
            settings.Folders = TargetFolders.ToList();
            settings.AutoIndex = AutoIndex;
            settings.VideoAutoSegment = VideoAutoSegment;
            settings.UseFfmpegDecoder = UseFfmpegDecoder;
            settings.UseFfmpegWorker = UseFfmpegWorker;
            settings.IndexImages = IndexImages;
            settings.IndexPdfs = IndexPdfs;
            settings.IndexVideos = IndexVideos;
            settings.IndexDocuments = IndexDocuments;
            settings.FolderSortIndex = FolderSortIndex;
            if (VideoSegmentIndex >= 0 && VideoSegmentIndex < VideoSegmentChoices.Count)
            {
                settings.VideoSegmentSeconds = VideoSegmentChoices[VideoSegmentIndex];
            }
            _store.Save(settings);
            _watcher.Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.T("Library.SettingsSaveFailed", ex.Message);
        }
    }
}
