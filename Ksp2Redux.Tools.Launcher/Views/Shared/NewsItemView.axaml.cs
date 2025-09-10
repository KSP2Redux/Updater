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

    private async void NewsPanel_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (VisualRoot is MainWindow { DataContext: MainWindowViewModel mainWindowViewModel } mainWindow)
        {
            // set selected news to this
            await mainWindowViewModel.CommunityTab.SetSelectedNewsId(News.GetNewsId(ViewModel?.News));
            // change to community tab if not already there
            mainWindow.MainTabControl.SelectedItem = mainWindow.CommunityTab;
        }
    }
}