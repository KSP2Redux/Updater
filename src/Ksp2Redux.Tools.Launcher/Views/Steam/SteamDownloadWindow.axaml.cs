using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Ksp2Redux.Tools.Launcher.ViewModels.Steam;

namespace Ksp2Redux.Tools.Launcher.Views.Steam;

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

            // Stop the download first, then let the Completed event close the window once it has wound down.
            e.Cancel = true;
            viewModel.CloseCommand.Execute(null);
        };
    }
}
