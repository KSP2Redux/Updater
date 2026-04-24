using Avalonia.Controls;
using Avalonia.Input;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Shared;

namespace Ksp2Redux.Tools.Launcher.Views.Shared;

public partial class NewsItemView : UserControl
{
    private NewsItemViewModel? ViewModel => (NewsItemViewModel?)DataContext;
    
    public NewsItemView()
    {
        InitializeComponent();
    }

    private void NewsPanel_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow { DataContext: MainWindowViewModel mainWindowViewModel } mainWindow)
            return;

        mainWindowViewModel.CommunityTab.SetSelectedNewsId(ViewModel?.NewsId ?? -1);
        mainWindow.MainTabControl.SelectedItem = mainWindow.CommunityTab;
    }
}