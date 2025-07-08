using System.Configuration;
using System.Data;
using System.Windows;

namespace Ksp2Redux.Tools;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeMode = ThemeMode.System;
    }
}