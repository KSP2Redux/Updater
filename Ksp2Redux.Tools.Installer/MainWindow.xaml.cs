using System.IO;
using System.Windows;
using System.Windows.Controls;
using Ksp2Redux.Tools.Common;
using Microsoft.Win32;
using Exception = System.Exception;

namespace Ksp2Redux.Tools;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    #region File Paths

    private const string AssemblyCSharpLocation = @"KSP2_x64_Data\Managed\Assembly-CSharp.dll";

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
        bool? result = dialog.ShowDialog();
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
        bool? result = dialog.ShowDialog();
        if (result == true)
        {
            TargetFolder.Text = dialog.FolderName;
        }
    }

    private void BrowsePatchFile_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            InitialDirectory = File.Exists(PatchFile.Text) ? new FileInfo(PatchFile.Text).Directory?.FullName ?? "" : "",
            FileName = File.Exists(PatchFile.Text) ? new FileInfo(PatchFile.Text).Name : "",
            Title = "Patch File"
        };
        bool? result = dialog.ShowDialog();
        if (result == true)
        {
            PatchFile.Text = dialog.FileName;
        }
    }

    // This is done when you press install

    private bool ValidateDirectories()
    {
        string installFolder = StockFolderTrimmed;
        if (!Directory.Exists(installFolder))
        {
            MessageBox.Show(
                $"KSP2 Install folder does not exist! ({installFolder})",
                "Error applying patch!"
            );
            return false;
        }

        if (!File.Exists(installFolder + "\\" + AssemblyCSharpLocation))
        {
            MessageBox.Show(
                $"KSP2 Assembly-CSharp does not exist at {installFolder}\\{AssemblyCSharpLocation}!",
                "Error applying patch!"
            );
            return false;
        }

        if (!File.Exists(PatchFile.Text))
        {
            MessageBox.Show(
                $"Patch file does not exist at ({PatchFile.Text})!",
                "Error applying patch!"
            );
            return false;
        }

        if (!Directory.Exists(TargetFolderTrimmed))
        {
            MessageBox.Show(
                $"Target folder does not exist! ({TargetFolderTrimmed})",
                "Error applying patch!"
            );
            return false;
        }

        return true;
    }

    private bool _isCurrentlyRunningPatch = false;


    private async void UpdateInstall_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!ValidateDirectories())
            {
                return;
            }

            if (_isCurrentlyRunningPatch)
            {
                MessageBox.Show("Already applying patch, please wait!");
                return;
            }

            _isCurrentlyRunningPatch = true;
            PatchLog.Text = "Beginning Patch!\n";
            Ksp2Patch patchFile = Ksp2Patch.FromFile(PatchFile.Text);
            bool errored = false;

            await patchFile.AsyncCopyAndApply(
                StockFolderTrimmed,
                TargetFolderTrimmed,
                LogToUI,
                _ => errored = true
            );

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

    private void LogToUI(string message)
    {
        if (PatchLog.Dispatcher.CheckAccess())
        {
            PatchLog.Text += $"{message}\n";
        }
        else
        {
            PatchLog.Dispatcher.Invoke(() => PatchLog.Text += $"{message}\n");
        }
    }
}