using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer_App.Controls;

/// <summary>Shows the matched text of an Office file (the file itself is opened in its own app on request).</summary>
public sealed partial class DocumentPreviewControl : UserControl
{
    private SearchResultItem? _currentItem;

    public DocumentPreviewControl()
    {
        InitializeComponent();
    }

    public void LoadItem(SearchResultItem item)
    {
        _currentItem = item;
        FileNameTextBlock.Text = item.Asset.FileName;
        BodyTextBlock.Text = item.Chunk.SnippetText is { Length: > 0 } text ? text : Loc.T("PdfPreview.NoText");

        string ext = Path.GetExtension(item.Asset.FilePath).ToLowerInvariant();
        int? number = item.Chunk.PageNumber;
        LocationTextBlock.Text = (ext, number) switch
        {
            (".pptx", { } n) => Loc.T("DocPreview.Slide", n, item.Asset.DurationOrPages ?? n),
            (".xlsx", { } n) => Loc.T("DocPreview.Sheet", n, item.Asset.DurationOrPages ?? n),
            _ => Loc.T("DocPreview.Document")
        };

        bool exists = File.Exists(item.Asset.FilePath);
        MessageTextBlock.Text = exists ? string.Empty : Loc.T("Common.FileNotFound");
        MessageTextBlock.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenInExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem != null) EmbeddingSearchViewer_App.Services.ExplorerLauncher.Reveal(_currentItem.Asset.FilePath);
    }

    private void OpenExternalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem == null || !File.Exists(_currentItem.Asset.FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _currentItem.Asset.FilePath, UseShellExecute = true });
        }
        catch { }
    }
}
