using Avalonia.Data.Converters;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Converters;

public static class ChannelConverters
{
    public static readonly IValueConverter DisplayName =
        new FuncValueConverter<string?, string>(ReleaseChannels.DisplayName);
}
