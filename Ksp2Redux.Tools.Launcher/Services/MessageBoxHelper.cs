using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using MsBox.Avalonia.Base;

namespace Ksp2Redux.Tools.Launcher.Services;

public static class MessageBoxHelper
{
    private static Window? OwnerWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static async Task<T> ShowAsOwnedAsync<T>(this IMsBox<T> box)
    {
        var owner = OwnerWindow;
        return owner is not null
            ? await box.ShowWindowDialogAsync(owner)
            : await box.ShowAsync();
    }
}
