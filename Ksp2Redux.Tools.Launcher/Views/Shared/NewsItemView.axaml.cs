using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Shared;
using Ksp2Redux.Tools.Launcher.Views;

namespace Ksp2Redux.Tools.Launcher.Views.Shared;

public partial class NewsItemView : UserControl
{
    private NewsItemViewModel? ViewModel => (NewsItemViewModel?)DataContext;
    
    public NewsItemView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void NewsPanel_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow { DataContext: MainWindowViewModel mainWindowViewModel } mainWindow)
            return;

        var mainTabControl = mainWindow.FindControl<TabControl>("MainTabControl");
        var communityTab = mainWindow.FindControl<TabItem>("CommunityTab");
        if (mainTabControl is null || communityTab is null) return;

        mainWindowViewModel.CommunityTab.SetSelectedNewsId(ViewModel?.NewsId ?? -1);
        mainTabControl.SelectedItem = communityTab;
    }
}
