using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer_App.Services;
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

    // The saved place is restored once; the content can be loaded again later.
    private bool _placementRestored;

    public MainWindow()
    {
        InitializeComponent();
        Title = Loc.T("App.Title");
        var iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        AppWindow.SetIcon(System.IO.File.Exists(iconPath) ? iconPath : "Assets/AppIcon.ico");

        // The default: the standard size, at the place Windows chooses.
        AppWindow.Resize(DefaultSize);

        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange) RememberNormalBounds();
        };
        AppWindow.Closing += (_, _) => SavePlacement();

        // The saved place is restored after the window is up: reading the screens and the file may take a moment,
        // and the start must not wait for them.
        if (Content is FrameworkElement root) root.Loaded += OnContentLoaded;
    }

    private void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        if (_placementRestored) return;
        _placementRestored = true;
        _ = RestorePlacementAsync();
    }

    /// <summary>Puts the window where it was at the last exit, unless that is no longer sensible on the current screens.</summary>
    private async Task RestorePlacementAsync()
    {
        (List<ScreenRect> Screens, WindowPlacement? Saved)? data = null;
        try
        {
            // The screens and the file are read on a worker thread: both may take a moment.
            data = await Task.Run(ReadPlacement);
        }
        catch (Exception ex)
        {
            // The screens can only be read from the UI thread now and then: try again on this thread.
            CrashLog.Write("Window placement (background)", ex);
            try
            {
                data = ReadPlacement();
            }
            catch (Exception retry)
            {
                CrashLog.Write("Window placement", retry);
            }
        }

        if (data == null) return; // the default place is kept

        var (screens, saved) = data.Value;

        // Everything below touches the window, so it happens on the UI thread.
        void Apply()
        {
            var placement = WindowPlacementValidator.Validate(saved, screens);
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

        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(() => Apply());
    }

    /// <summary>The screens of the moment and the placement of the last exit.</summary>
    private (List<ScreenRect> Screens, WindowPlacement? Saved) ReadPlacement()
    {
        // Indexed access: the list is a WinRT vector view, which does not take part in LINQ.
        var areas = DisplayArea.FindAll();
        var screens = new List<ScreenRect>();
        for (int i = 0; i < areas.Count; i++)
        {
            var work = areas[i].WorkArea;
            screens.Add(new ScreenRect(work.X, work.Y, work.Width, work.Height));
        }

        return (screens, _placementStore.Load());
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
