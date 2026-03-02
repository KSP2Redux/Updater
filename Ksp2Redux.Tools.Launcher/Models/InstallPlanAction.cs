namespace Ksp2Redux.Tools.Launcher.Models;

public enum InstallPlanAction
{
    // This just "uninstalls" completely
    Uninstall,
    // This takes "uninstall.zip"
    RevertToStock,
    // This finds the prepatched version for the distribution (after a revert-to-stock, or from stock)
    // If "uninstall.zip" already exists, it does not recreate it
    Prepatch, 
    ApplyPatchFile,
    // This is used for copying the install directory from another directory (mostly used on first install to make 2 copies)
    CopyFrom
}