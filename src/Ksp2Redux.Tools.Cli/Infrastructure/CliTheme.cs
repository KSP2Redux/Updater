using Spectre.Console;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// The launcher's palette, expressed as console colors. Mirrors design/design-system/src/tokens.css.
/// </summary>
public static class CliTheme
{
    /// <summary>
    /// The brand red, used for errors and the application title.
    /// </summary>
    public static readonly Color BRAND_RED = new(0xDC, 0x15, 0x01);

    /// <summary>
    /// The brand orange, used for headings and highlighted rows.
    /// </summary>
    public static readonly Color BRAND_ORANGE = new(0xFC, 0x76, 0x00);

    /// <summary>
    /// The success green, used for a healthy feed or a finished install.
    /// </summary>
    public static readonly Color SUCCESS = new(0x2E, 0xA0, 0x43);

    /// <summary>
    /// The danger red, used for a failed feed or a failed step.
    /// </summary>
    public static readonly Color DANGER = new(0xB3, 0x3A, 0x3A);

    /// <summary>
    /// The warning amber, used for warnings raised out of the launcher services.
    /// </summary>
    public static readonly Color WARNING = new(0xD9, 0xA4, 0x41);

    /// <summary>
    /// The secondary grey, used for ids, paths and other supporting detail.
    /// </summary>
    public static readonly Color SECONDARY = new(0x88, 0x88, 0x88);

    /// <summary>
    /// The style applied to table headers.
    /// </summary>
    public const string HEADER_STYLE = "bold #FC7600";

    /// <summary>
    /// The style applied to supporting detail such as ids and paths.
    /// </summary>
    public const string DETAIL_STYLE = "#888888";

    /// <summary>
    /// The style applied to the row describing the active install.
    /// </summary>
    public const string ACTIVE_STYLE = "bold #2EA043";
}
