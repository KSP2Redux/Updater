using System;

namespace Ksp2Redux.Tools.Launcher.Models;

public class News
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Date { get; set; }
    public string Author { get; set; } = "";
    public string Link { get; set; } = "";
}