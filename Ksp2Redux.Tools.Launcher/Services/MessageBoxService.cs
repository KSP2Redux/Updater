using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using MsBox.Avalonia;
using MsBox.Avalonia.Base;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface IMessageBoxService
{
    Task<ButtonResult> ShowMessageBoxAsOwnedAsync(
        string title,
        string text,
        ButtonEnum @enum = ButtonEnum.Ok,
        Icon icon = Icon.None,
        object? context = null,
        WindowStartupLocation windowStartupLocation = WindowStartupLocation.CenterScreen);
}

public class MessageBoxService : IMessageBoxService
{
#pragma warning disable RS0030
    private static Window? OwnerWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task<ButtonResult> ShowMessageBoxAsOwnedAsync(
        string title,
        string text,
        ButtonEnum @enum = ButtonEnum.Ok,
        Icon icon = Icon.None,
        object? context = null,
        WindowStartupLocation windowStartupLocation = WindowStartupLocation.CenterScreen)
    {
        IMsBox<ButtonResult> box = MessageBoxManager.GetMessageBoxStandard(title, text, @enum, icon, context, windowStartupLocation);
        return await ShowAsOwnedAsync(box);
    }
    
    private async Task<T> ShowAsOwnedAsync<T>(IMsBox<T> box)
    {
        var owner = OwnerWindow;

        return owner is not null
            ? await box.ShowWindowDialogAsync(owner)
            : await box.ShowAsync();
    }
#pragma warning restore RS0030
}
