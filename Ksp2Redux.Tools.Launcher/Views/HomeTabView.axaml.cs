using Avalonia.Controls;
using Avalonia.Interactivity;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using System.ComponentModel;
using System.Threading.Tasks;

namespace Ksp2Redux.Tools.Launcher.Views;

public partial class HomeTabView : UserControl
{
    private HomeTabViewModel? Model => (DataContext as HomeTabViewModel);

    public HomeTabView()
    {
        InitializeComponent();
        Loaded += RefreshAll;
    }

    private async void RefreshAll(object? sender, RoutedEventArgs e)
    {
        if (Model is not null)
        {
            await Model.UpdateVersionsList();
            ShowButton(Model.MainButtonShown);
            Model.PropertyChanged += ReactToHomeTabPropertyChanged;
        }
    }

    private void ReactToHomeTabPropertyChanged(object? sender, PropertyChangedEventArgs? e)
    {
        if (Model is not null)
        {
            ShowButton(Model.MainButtonShown);
        }
    }

    private void ShowButton(HomeTabViewModel.MainButtonState which)
    {
        LaunchButton.IsVisible = false;
        UpdateButton.IsVisible = false;
        CancelButton.IsVisible = false;
        InstallButton.IsVisible = false;
        switch (which)
        {
            case HomeTabViewModel.MainButtonState.Launch:
                LaunchButton.IsVisible = true;
                break;
            case HomeTabViewModel.MainButtonState.Install:
                InstallButton.IsVisible = true;
                break;
            case HomeTabViewModel.MainButtonState.Update:
                UpdateButton.IsVisible = true;
                break;
            case HomeTabViewModel.MainButtonState.Cancel:
                CancelButton.IsVisible = true;
                break;
        }
    }
}