using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Ksp2Redux.Tools.Launcher.ViewModels.Steam;

namespace Ksp2Redux.Tools.Launcher.Views.Steam;

/// <summary>Downloads KSP2 from Steam. Closes with the new install's exe path, or null if it did not finish.</summary>
public partial class SteamDownloadWindow : Window
{
    private bool _completed;

    public SteamDownloadWindow() => AvaloniaXamlLoader.Load(this);

    public SteamDownloadWindow(SteamDownloadViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.Completed += (_, exePath) =>
        {
            if (_completed) return;
            _completed = true;
            Close(exePath);
        };
        Opened += async (_, _) => await viewModel.StartAsync();
        Closing += (_, e) =>
        {
            if (_completed || !viewModel.IsRunning) return;

            // Cancel the download and let Completed close the window once it has stopped.
            e.Cancel = true;
            viewModel.CloseCommand.Execute(null);
        };
    }
}
