using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace Ksp2Redux.Tools.Launcher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            DisableWindowResize();
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

        nint hwnd = handle.Handle;
        int style = GetWindowLong(hwnd, GWL_STYLE);

        // Remove resizing
        style &= ~WS_THICKFRAME;

        SetWindowLong(hwnd, GWL_STYLE, style);
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

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);
    private static nint _originalWndProc;
    private static nint _hwnd;
    private static readonly WndProcDelegate NewWndProcDelegate = CustomWndProc;

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
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint newProc);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);
}