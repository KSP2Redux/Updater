namespace Ksp2Redux.Tools.Common;

public class PatchOperation
{
    public required string fileName;
    public required PatchAction action;
    public byte[]? originalHash;
    public byte[]? finalHash;

    public enum PatchAction
    {
        Patch,
        Add,
        Remove,
    }
}
