using CommunityToolkit.Mvvm.ComponentModel;
using EmbeddingSearchViewer.Core.Localization;

namespace EmbeddingSearchViewer_App.ViewModels;

/// <summary>A folder in the list of folders to index, with the number of files indexed under it.</summary>
public partial class FolderEntry : ObservableObject
{
    public string Path { get; }

    [ObservableProperty]
    private string _countText = string.Empty;

    public FolderEntry(string path)
    {
        Path = path;
    }

    public void SetCount(int count) => CountText = Loc.T("Library.Folder.Count", count);
}
