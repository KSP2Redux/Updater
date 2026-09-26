using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Ksp2Redux.Tools.Launcher.ViewModels.Steam;

namespace Ksp2Redux.Tools.Launcher.Views.Steam;

/// <summary>Signs the player in to Steam. Closes with true if sign-in succeeded.</summary>
public partial class SteamSignInWindow : Window
{
    private bool _completed;

    public SteamSignInWindow() => AvaloniaXamlLoader.Load(this);

    public SteamSignInWindow(SteamSignInViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.Completed += (_, signedIn) =>
        {
            if (_completed) return;
            _completed = true;
            Close(signedIn);
        };
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SteamSignInViewModel.Stage) && viewModel.IsEnteringGuardCode)
            {
                this.FindControl<TextBox>("GuardCodeBox")?.Focus();
            }
        };
        Opened += (_, _) =>
        {
            viewModel.Start();
            this.FindControl<TextBox>("AccountNameBox")?.Focus();
        };
        Closing += (_, _) =>
        {
            if (_completed) return;
            _completed = true;
            viewModel.Cancel();
        };
    }
}
