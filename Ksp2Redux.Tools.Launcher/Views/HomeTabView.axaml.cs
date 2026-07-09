using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;

namespace Ksp2Redux.Tools.Launcher.Views;

public partial class HomeTabView : UserControl
{
    private HomeTabViewModel? Model => DataContext as HomeTabViewModel;
    private TextBox? InstallLogTextBoxControl => this.FindControl<TextBox>("InstallLogTextBox");
    public Border? GlassPanelBorder => this.FindControl<Border>("GlassPanel");

    public HomeTabView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void TriggerInstall(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.UpdateLauncher();
    }

    private void InstallLogTextBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (InstallLogTextBoxControl is not { } installLogTextBox) return;
        var lastLineIndex = Math.Max(0, installLogTextBox.GetLineCount() - 1);
        installLogTextBox.ScrollToLine(lastLineIndex);
    }

    // Same navigation the sidebar news items use (NewsItemView.NewsPanel_OnPointerPressed):
    // select the article on the Community tab, then switch to it.
    private void ReadFullNotes_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow { DataContext: MainWindowViewModel mainWindowViewModel })
            return;
        if (Model?.FeaturedNewsId is not { } newsId) return;

        mainWindowViewModel.CommunityTab.SetSelectedNewsId(newsId);
        mainWindowViewModel.CurrentTab = MainWindowViewModel.CommunityTabId;
    }
}
