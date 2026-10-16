using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer_App.Controls;

public sealed partial class VideoPreviewControl : UserControl
{
    private SearchResultItem? _currentItem;
    private MediaPlayer? _mediaPlayer;

    public VideoPreviewControl()
    {
        InitializeComponent();
    }

    public void LoadItem(SearchResultItem item)
    {
        _currentItem = item;
        if (!File.Exists(item.Asset.FilePath))
        {
            FileInfoTextBlock.Text = "動画ファイルが見つかりません: " + item.Asset.FilePath;
            return;
        }

        FileInfoTextBlock.Text = $"{item.Asset.FileName} ({item.Asset.FileSizeBytes / (1024 * 1024)} MB)";

        int startMs = item.Chunk.StartTimeMs ?? 0;
        var startTs = TimeSpan.FromMilliseconds(startMs);
        HitMomentTextBlock.Text = $"一致箇所へジャンプ ({startTs:mm\\:ss})";

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
            FileInfoTextBlock.Text = $"動画読み込み失敗: {ex.Message}";
        }
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
