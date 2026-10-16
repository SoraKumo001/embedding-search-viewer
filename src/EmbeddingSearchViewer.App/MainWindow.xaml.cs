using System.Collections.Generic;
using EmbeddingSearchViewer.Infrastructure.Library;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace EmbeddingSearchViewer_App;

public sealed partial class MainWindow : Window
{
    private static readonly SizeInt32 DefaultSize = new(1280, 840);

    private readonly WindowPlacementStore _placementStore = new();

    // Bounds of the window while it is in the normal state; a maximized window reports the whole screen instead.
    private RectInt32? _normalBounds;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // The default: the standard size, at the place Windows chooses.
        AppWindow.Resize(DefaultSize);
        RestorePlacement();

        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange) RememberNormalBounds();
        };
        AppWindow.Closing += (_, _) => SavePlacement();
    }

    /// <summary>Puts the window where it was at the last exit, unless that is no longer sensible on the current screens.</summary>
    private void RestorePlacement()
    {
        // Indexed access: the list is a WinRT vector view, which does not take part in LINQ.
        var areas = DisplayArea.FindAll();
        var screens = new List<ScreenRect>();
        for (int i = 0; i < areas.Count; i++)
        {
            var work = areas[i].WorkArea;
            screens.Add(new ScreenRect(work.X, work.Y, work.Width, work.Height));
        }

        var placement = WindowPlacementValidator.Validate(_placementStore.Load(), screens);
        if (placement == null)
        {
            RememberNormalBounds();
            return;
        }

        AppWindow.MoveAndResize(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height));
        RememberNormalBounds();
        if (placement.IsMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private void RememberNormalBounds()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _normalBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        }
    }

    private void SavePlacement()
    {
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        RememberNormalBounds(); // a no-op unless the window is in the normal state right now

        if (_normalBounds is not { } bounds) return;
        _placementStore.Save(new WindowPlacement(bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized));
    }
}
