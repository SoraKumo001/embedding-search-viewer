using EmbeddingSearchViewer.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbeddingSearchViewer_App.Localization;

/// <summary>
/// Gives an element its texts from the resources, like x:Uid does: <c>loc:L.Uid="Search.Title"</c> sets every property
/// that has a key "Search.Title.&lt;Property&gt;" (Text, Content, Header, PlaceholderText, ToolTip, ...) to the text of
/// the language the app was started in.
/// </summary>
public static class L
{
    public static readonly DependencyProperty UidProperty =
        DependencyProperty.RegisterAttached("Uid", typeof(string), typeof(L), new PropertyMetadata(null, OnUidChanged));

    public static string? GetUid(DependencyObject element) => (string?)element.GetValue(UidProperty);

    public static void SetUid(DependencyObject element, string? value) => element.SetValue(UidProperty, value);

    private static void OnUidChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not string uid || uid.Length == 0) return;

        foreach (var (property, value) in Loc.ForUid(uid))
        {
            Apply(element, property, value);
        }
    }

    private static void Apply(DependencyObject element, string property, string value)
    {
        if (property == "ToolTip")
        {
            ToolTipService.SetToolTip(element, value);
            return;
        }

        var info = element.GetType().GetProperty(property);
        if (info is { CanWrite: true })
        {
            info.SetValue(element, value);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"Localization: {element.GetType().Name} has no property '{property}' (uid {GetUid(element)}).");
        }
    }
}
