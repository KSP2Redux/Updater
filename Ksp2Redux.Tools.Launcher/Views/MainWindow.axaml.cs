using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
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
            DisableWindowResize();
            ApplyNativeRoundedCorners();
            SetCustomWndProc();
            RefreshBackdropClips();
        };

        // Which panel is "active" changes on every tab switch and every time Home's log
        // or Community's article shows/hides, none of which fire a single one-shot event
        // we can hook. Recomputing on every layout pass keeps it in sync regardless of
        // what caused the layout change.
        LayoutUpdated += (_, _) => RefreshBackdropClips();
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
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void DisableWindowResize()
    {
        IPlatformHandle? handle = TryGetPlatformHandle();
        if (handle is null || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        const int GWL_STYLE = -16;
        const int WS_THICKFRAME = 0x00040000;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_FRAMECHANGED = 0x0020;

        nint hwnd = handle.Handle;
        int style = GetWindowLong(hwnd, GWL_STYLE);

        // Remove resizing
        style &= ~WS_THICKFRAME;

        SetWindowLong(hwnd, GWL_STYLE, style);

        // SetWindowLong alone doesn't make Windows recompute the frame; without this,
        // it can keep using metrics cached under the old WS_THICKFRAME style.
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private void SetCustomWndProc()
    {
        IPlatformHandle? handle = TryGetPlatformHandle();
        if (handle is null || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        IntPtr hwnd = handle.Handle;
        _originalWndProc = GetWindowLongPtr(hwnd, GWLP_WNDPROC);
        _hwnd = hwnd;

        // replace WndProc with a custom one
        SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(NewWndProcDelegate));
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

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);
    private static nint _originalWndProc;
    private static nint _hwnd;
    private static readonly WndProcDelegate NewWndProcDelegate = CustomWndProc;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    private static nint CustomWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        const uint WM_NCHITTEST = 0x0084;
        const int HTCLIENT = 1;

        return msg == WM_NCHITTEST
            ? HTCLIENT // Always return client area — no resize zones
            : CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam); // Call original WndProc for everything else
    }

    // Win32 interop
    private const int GWLP_WNDPROC = -4;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint newProc);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

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
