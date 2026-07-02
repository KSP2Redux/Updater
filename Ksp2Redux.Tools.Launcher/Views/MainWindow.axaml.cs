using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace Ksp2Redux.Tools.Launcher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += (_, _) =>
        {
            DisableWindowResize();
            ApplyNativeRoundedCorners();
            SetCustomWndProc();
        };
    }

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
