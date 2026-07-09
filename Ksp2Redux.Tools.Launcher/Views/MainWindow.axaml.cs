using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels;

namespace Ksp2Redux.Tools.Launcher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        // Must call the generated InitializeComponent, not just AvaloniaXamlLoader.Load
        // directly: InitializeComponent also runs the NameScope.Find<T> calls that
        // populate every Name="..." field (ContentBackdropBlur, MainTabControl, Sidebar,
        // etc.). Loading the XAML without it still renders the UI fine, but leaves those
        // fields permanently null, so any code referencing them (e.g. the backdrop-blur
        // clip calculations below) silently no-ops.
        InitializeComponent();
        Opened += (_, _) =>
        {
            ApplyNativeRoundedCorners();
            RestoreWindowPlacement();
            UpdateMaximizedState();
            RefreshBackdropClips();
        };
        PositionChanged += (_, e) =>
        {
            if (WindowState == WindowState.Normal) _lastNormalPosition = e.Point;
        };
        Closing += (_, _) => SaveWindowPlacement();

        // The title-bar nav buttons aren't TabItems, so tab selection can't style them
        // via :selected - mirror CurrentTab onto an "active" style class instead.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.CurrentTab)) UpdateActiveNavTab();
            };
            UpdateActiveNavTab();
        };

        // Which panel is "active" changes on every tab switch and every time Home's log
        // or Community's article shows/hides, none of which fire a single one-shot event
        // we can hook. Recomputing on every layout pass keeps it in sync regardless of
        // what caused the layout change.
        LayoutUpdated += (_, _) => RefreshBackdropClips();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            UpdateMaximizedState();
        }
        else if (change.Property == ClientSizeProperty && WindowState == WindowState.Normal)
        {
            _lastNormalSize = ClientSize;
        }
    }

    // The size/position to persist must be the NORMAL bounds even when the window closes
    // maximized or minimized - saving the maximized rect would make restore-from-maximized
    // snap to a full-screen-sized "normal" window.
    private PixelPoint _lastNormalPosition;
    private Size _lastNormalSize;

    private void RestoreWindowPlacement()
    {
        _lastNormalPosition = Position;
        _lastNormalSize = ClientSize;

        if (DataContext is not MainWindowViewModel vm) return;
        var workingAreas = Screens.All.Select(s => s.WorkingArea).ToList();
        var placement = vm.GetRestoredWindowPlacement(workingAreas, MinWidth, MinHeight);
        if (placement is null) return;

        Position = new PixelPoint(placement.X, placement.Y);
        Width = placement.Width;
        Height = placement.Height;
        _lastNormalPosition = Position;
        _lastNormalSize = new Size(placement.Width, placement.Height);
        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowPlacement()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var normal = WindowState == WindowState.Normal;
        var position = normal ? Position : _lastNormalPosition;
        var size = normal ? ClientSize : _lastNormalSize;
        vm.SaveWindowPlacement(new WindowPlacement
        {
            X = position.X,
            Y = position.Y,
            Width = size.Width,
            Height = size.Height,
            IsMaximized = WindowState == WindowState.Maximized,
        });
    }

    // The red 2px frame deliberately stays in every state (brand signature); only the
    // corner rounding drops at maximize, since the OS squares the window anyway and a
    // rounded clip would leave black notches at the screen edges.
    private void UpdateMaximizedState()
    {
        var maximized = WindowState == WindowState.Maximized;
        OuterFrame?.Classes.Set("maximized", maximized);
        InnerChrome?.Classes.Set("maximized", maximized);
        this.FindControl<Border>("TitleBar")?.Classes.Set("maximized", maximized);
        if (ResizeGrips is not null) ResizeGrips.IsVisible = !maximized;
        if (MaximizeGlyph is not null) MaximizeGlyph.Text = maximized ? "❐" : "◻";
        if (MaximizeButton is not null) ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    private void UpdateActiveNavTab()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        HomeNavButton?.Classes.Set("active", vm.CurrentTab == MainWindowViewModel.HomeTabId);
        CommunityNavButton?.Classes.Set("active", vm.CurrentTab == MainWindowViewModel.CommunityTabId);
        ModsNavButton?.Classes.Set("active", vm.CurrentTab == MainWindowViewModel.ModsTabId);
        SettingsNavButton?.Classes.Set("active", vm.CurrentTab == MainWindowViewModel.SettingsTabId);
    }

    private void RefreshBackdropClips()
    {
        UpdateContentBackdropClip();
        UpdateSidebarBackdropClip();
    }

    private void UpdateContentBackdropClip()
    {
        if (ContentBackdropBlur is null) return;

        // Deriving the panel's rect from MainTabControl.Bounds minus an assumed margin
        // used to drift out of sync with reality: the Fluent TabControl template adds
        // its own TabItemMargin padding (and the header row's height) on top of that,
        // so the computed clip ended up larger than the panel's actual rendered bounds
        // and the blur bled past its border. Reading the active panel's own Bounds
        // directly sidesteps needing to know any of that.
        var panel = GetActiveGlassPanel();
        if (panel is null || panel.Bounds.Width <= 0 || panel.Bounds.Height <= 0) return;

        var transform = panel.TransformToVisual(ContentBackdropBlur);
        if (transform is null) return;

        var topLeft = transform.Value.Transform(new Point(0, 0));
        var rect = new Rect(topLeft, panel.Bounds.Size);

        ContentBackdropBlur.Clip = new RectangleGeometry(rect, RadiusLarge, RadiusLarge);
    }

    private Border? GetActiveGlassPanel() => MainTabControl?.SelectedIndex switch
    {
        MainWindowViewModel.HomeTabId => this.FindControl<HomeTabView>("HomeView")?.GlassPanelBorder,
        MainWindowViewModel.CommunityTabId => this.FindControl<CommunityTabView>("CommunityView")?.GlassPanelBorder,
        MainWindowViewModel.ModsTabId => this.FindControl<ModsTabView>("ModsView")?.GlassPanelBorder,
        MainWindowViewModel.SettingsTabId => this.FindControl<SettingsTabView>("SettingsView")?.GlassPanelBorder,
        _ => null,
    };

    private void UpdateSidebarBackdropClip()
    {
        if (SidebarBackdropBlur is null) return;
        var newsPanel = Sidebar?.NewsPanelBorder;
        if (newsPanel is null || newsPanel.Bounds.Width <= 0 || newsPanel.Bounds.Height <= 0) return;

        var transform = newsPanel.TransformToVisual(SidebarBackdropBlur);
        if (transform is null) return;

        var topLeft = transform.Value.Transform(new Point(0, 0));
        var rect = new Rect(topLeft, newsPanel.Bounds.Size);

        SidebarBackdropBlur.Clip = new RectangleGeometry(rect, RadiusLarge, RadiusLarge);
    }

    private double RadiusLarge =>
        this.TryFindResource("RadiusLarge", out var radiusValue) && radiusValue is CornerRadius cornerRadius
            ? cornerRadius.TopLeft
            : 12;

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }

        BeginMoveDrag(e);
    }

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is not Control { Tag: string tag } || !Enum.TryParse<WindowEdge>(tag, out var edge)) return;
        BeginResizeDrag(edge, e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object? sender, RoutedEventArgs e)
    {
        ToggleMaximized();
    }

    private void ToggleMaximized()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyNativeRoundedCorners()
    {
        IPlatformHandle? handle = TryGetPlatformHandle();
        if (handle is null || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // Only allowed here because it is not important for testing (and because services are not accessible from views)
        // this method could be moved to the ViewModel and use IOperatingSystemService instead
#pragma warning disable RS0030
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
#pragma warning restore RS0030
        {
            var preference = (int)DwmWindowCornerPreference.Round;
            _ = DwmSetWindowAttribute(
                handle.Handle,
                DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference,
                Marshal.SizeOf<int>());
        }
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private enum DwmWindowCornerPreference
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3,
    }
}
