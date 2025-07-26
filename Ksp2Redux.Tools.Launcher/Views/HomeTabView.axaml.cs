using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Ksp2Redux.Tools.Launcher.Views;

public partial class HomeTabView : UserControl
{
    public HomeTabView()
    {
        InitializeComponent();
    }

    private void InstallButton_Click(object? sender, RoutedEventArgs e)
    {
        InstallButton.IsVisible = false;
        UpdateButton.IsVisible = true;
    }

    private void UpdateButton_Click(object? sender, RoutedEventArgs e)
    {
        UpdateButton.IsVisible = false;
        CancelButton.IsVisible = true;
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        CancelButton.IsVisible = false;
        LaunchButton.IsVisible = true;
    }

    private void LaunchButton_Click(object? sender, RoutedEventArgs e)
    {
        LaunchButton.IsVisible = false;
        InstallButton.IsVisible = true;
    }
}