using Avalonia.Controls;
using Avalonia.Interactivity;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using System.Diagnostics;

namespace Ksp2Redux.Tools.Launcher.Views;

public partial class SettingsTabView : UserControl
{
    private SettingsTabViewModel Model => (DataContext as SettingsTabViewModel)!;

    public SettingsTabView()
    {
        InitializeComponent();
    }

    public async void InstallDir_Click(object sender, RoutedEventArgs args)
    {
        await Model.SelectGameInstallDirectory();
        InstallPath.Text = Model.DisplayedInstallPath;
    }
}