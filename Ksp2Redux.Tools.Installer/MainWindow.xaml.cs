using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Ksp2Redux.Tools.Common;
using Microsoft.Win32;
using Exception = System.Exception;

namespace Ksp2Redux.Tools;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    #region File Paths

    private static string[] _copiedFolders = ["MonoBleedingEdge", "KSP2_x64_Data"];
    private static string[] _copiedFiles = ["KSP2_x64.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe"];

    private const string AssemblyCSharpLocation = @"KSP2_x64_Data\Managed\Assembly-CSharp.dll";
    private const string AssemblyCSharpBackupLocation = @"KSP2_x64_Data\Managed\Assembly-CSharp.unpatched";

    private string StockFolderTrimmed => Ksp2InstallFolder.Text.TrimEnd('\\','/');
    private string TargetFolderTrimmed => TargetFolder.Text.TrimEnd('\\', '/');
    
    #endregion
    
    public MainWindow()
    {
        InitializeComponent();
    }

    private void BrowseKsp2InstallFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = Ksp2InstallFolder.Text,
            Title = "KSP2 Install Folder"
        };
        var result = dialog.ShowDialog();
        if (result == true)
        {
            Ksp2InstallFolder.Text = dialog.FolderName;
        }
    }

    private void BrowseTargetFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = TargetFolder.Text,
            Title = "Folder to copy KSP2 install to"
        };
        var result = dialog.ShowDialog();
        if (result == true)
        {
            TargetFolder.Text = dialog.FolderName;
        }
    }

    private void CopyFiles_OnChecked(object sender, RoutedEventArgs e)
    {
        TargetFolder.IsEnabled = true;
        BrowseTargetFolder.IsEnabled = true;
    }

    private void CopyFiles_OnUnchecked(object sender, RoutedEventArgs e)
    {
        TargetFolder.IsEnabled = false;
        BrowseTargetFolder.IsEnabled = false;
    }

    private void BrowsePatchFile_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            InitialDirectory = File.Exists(PatchFile.Text) ? (new FileInfo(PatchFile.Text).Directory?.FullName ?? "") : "",
            FileName = File.Exists(PatchFile.Text) ? (new FileInfo(PatchFile.Text).Name) : "",
            Title = "Patch File"
        };
        var result = dialog.ShowDialog();
        if (result == true)
        {
            PatchFile.Text = dialog.FileName;
        }
    }

    // This is done when you press install

    private bool ValidateDirectories()
    {
        var installFolder = StockFolderTrimmed;
        if (!Directory.Exists(installFolder))
        {
            MessageBox.Show($"KSP2 Install folder does not exist! ({installFolder})", "Error applying patch!");   
            return false;
        }

        if (!File.Exists(installFolder + "\\" + AssemblyCSharpLocation))
        {
            MessageBox.Show($"KSP2 Assembly-CSharp does not exist at {installFolder + "\\" + AssemblyCSharpLocation}!",
                "Error applying patch!");
            return false;
        }

        if (!File.Exists(PatchFile.Text))
        {
            MessageBox.Show($"Patch file does not exist at ({PatchFile.Text})!",
                "Error applying patch!");
            return false;
        }

        if (CopyFiles.IsChecked == true && !Directory.Exists(TargetFolderTrimmed))
        {
            MessageBox.Show($"Target folder does not exist! ({TargetFolderTrimmed})", "Error applying patch!");
            return false;
        }
        return true;
    }

    private bool _isCurrentlyRunningPatch = false;
    
    
    private async void UpdateInstall_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!ValidateDirectories()) return;
            if (_isCurrentlyRunningPatch)
            {
                MessageBox.Show("Already applying patch, please wait!");
                return;
            }
            _isCurrentlyRunningPatch = true;
            PatchLog.Text = "Beginning Patch!\n";
            var patchFile = Ksp2Patch.FromFile(PatchFile.Text);
            bool errored = false;
            if (CopyFiles.IsChecked == true)
            {
                await patchFile.AsyncCopyAndApply(StockFolderTrimmed, TargetFolderTrimmed, x => PatchLog.Text += $"{x}\n");
            }
            else
            {
                await patchFile.AsyncApply(StockFolderTrimmed, StockFolderTrimmed, x => PatchLog.Text += $"{x}\n");
            }
            _isCurrentlyRunningPatch = false;
            if (!errored)
            {
                MessageBox.Show("Patch complete!");
            }
        }
        catch (Exception error)
        {
            _isCurrentlyRunningPatch = false;
            MessageBox.Show(error.Message, "Error applying patch!");
        }
    }

    private void PatchLog_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        ScrollLog.ScrollToEnd();
    }
}