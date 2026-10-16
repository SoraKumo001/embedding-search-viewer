using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer_App.Controls;

public sealed partial class VideoPreviewControl : UserControl
{
    private SearchResultItem? _currentItem;
    private MediaPlayer? _mediaPlayer;
    private string? _loadedPath;

    public VideoPreviewControl()
    {
        InitializeComponent();
    }

    public void LoadItem(SearchResultItem item)
    {
        _currentItem = item;
        _loadedPath = item.Asset.FilePath;
        if (!File.Exists(item.Asset.FilePath))
        {
            FileInfoTextBlock.Text = Loc.T("VideoPreview.FileNotFound", item.Asset.FilePath);
            return;
        }

        FileInfoTextBlock.Text = $"{item.Asset.FileName} ({item.Asset.FileSizeBytes / (1024 * 1024)} MB)";

        int startMs = item.Chunk.StartTimeMs ?? 0;
        var startTs = TimeSpan.FromMilliseconds(startMs);
        HitMomentTextBlock.Text = Loc.T("VideoPreview.JumpTo", startTs);

        // Initialize Player
        if (_mediaPlayer == null)
        {
            _mediaPlayer = new MediaPlayer();
            PlayerElement.SetMediaPlayer(_mediaPlayer);
        }

        try
        {
            var mediaSource = MediaSource.CreateFromUri(new Uri(item.Asset.FilePath));
            _mediaPlayer.Source = mediaSource;

            // Seek to match moment when opened
            _mediaPlayer.MediaOpened += OnMediaOpened;
        }
        catch (Exception ex)
        {
            FileInfoTextBlock.Text = Loc.T("VideoPreview.LoadFailed", ex.Message);
        }
    }

    /// <summary>Starts playing from the beginning of a scene (another scene of the video that is open, or of another video).</summary>
    public void JumpTo(SearchResultItem scene)
    {
        if (_mediaPlayer == null || !string.Equals(_loadedPath, scene.Asset.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            LoadItem(scene);
            return;
        }

        _currentItem = scene;
        var start = TimeSpan.FromMilliseconds(scene.Chunk.StartTimeMs ?? 0);
        HitMomentTextBlock.Text = Loc.T("VideoPreview.JumpTo", start);
        _mediaPlayer.PlaybackSession.Position = start;
        _mediaPlayer.Play();
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        sender.MediaOpened -= OnMediaOpened;
        if (_currentItem?.Chunk.StartTimeMs.HasValue == true)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var targetTime = TimeSpan.FromMilliseconds(_currentItem.Chunk.StartTimeMs.Value);
                sender.PlaybackSession.Position = targetTime;
            });
        }
    }

    private void JumpToHitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer != null && _currentItem?.Chunk.StartTimeMs.HasValue == true)
        {
            var targetTime = TimeSpan.FromMilliseconds(_currentItem.Chunk.StartTimeMs.Value);
            _mediaPlayer.PlaybackSession.Position = targetTime;
            _mediaPlayer.Play();
        }
    }

    private void OpenVideoExternalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem != null && File.Exists(_currentItem.Asset.FilePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _currentItem.Asset.FilePath,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    public void Cleanup()
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Pause();
            _mediaPlayer.Source = null;
        }
    }
}
