namespace Ksp2Redux.Tools.BundleConversion;

/// <summary>
/// What a journaled in-place edit finds in the bundle on disk.
/// </summary>
public enum EditState
{
    /// <summary>The file holds the converted bytes.</summary>
    Applied,

    /// <summary>The file holds the stock bytes, for example after a Steam file verification. The edit needs reapplying.</summary>
    Stock,

    /// <summary>The file holds neither, so something else changed it. The converter leaves it alone.</summary>
    Foreign,

    /// <summary>The bundle is missing, or its size no longer matches the journal.</summary>
    FileChanged,
}

/// <summary>
/// The state of one journaled in-place edit.
/// </summary>
/// <param name="File">The bundle file name.</param>
/// <param name="Offset">The edit's file offset.</param>
/// <param name="State">What the file holds there.</param>
public sealed record EditStatus(string File, long Offset, EditState State);

/// <summary>
/// What a journaled converted copy finds on disk.
/// </summary>
public enum OutputState
{
    /// <summary>The copy exists and its hash matches the journal.</summary>
    Ok,

    /// <summary>The copy does not exist.</summary>
    Missing,

    /// <summary>The copy exists but its hash differs from the journal.</summary>
    HashMismatch,

    /// <summary>The journal has no hash for it yet, because a conversion was interrupted while writing it.</summary>
    Pending,

    /// <summary>The stock bundle it was made from is missing or no longer matches, so the copy is stale.</summary>
    StockChanged,

    /// <summary>The journal entry does not describe a copy inside the conversion folder, so it was not touched.</summary>
    Invalid,
}

/// <summary>
/// The state of one journaled converted copy.
/// </summary>
/// <param name="File">The bundle file name.</param>
/// <param name="Output">The copy, relative to the game folder.</param>
/// <param name="State">What was found.</param>
public sealed record OutputStatus(string File, string Output, OutputState State);

/// <summary>
/// The outcome of a conversion run.
/// </summary>
public sealed class ConversionResult
{
    internal ConversionResult(ConversionPlan plan, bool dryRun, string journalPath)
    {
        Plan = plan;
        DryRun = dryRun;
        JournalPath = journalPath;
    }

    /// <summary>Gets the plan the run carried out.</summary>
    public ConversionPlan Plan { get; }

    /// <summary>Gets a value indicating whether the run only planned and wrote nothing.</summary>
    public bool DryRun { get; }

    /// <summary>Gets the journal path.</summary>
    public string JournalPath { get; }

    /// <summary>Gets the number of in-place edits written this run.</summary>
    public int EditsWritten { get; internal set; }

    /// <summary>Gets the number of in-place edits that were already applied.</summary>
    public int EditsAlreadyApplied { get; internal set; }

    /// <summary>Gets the number of earlier edits rewritten to a new target, for example after a manifest change.</summary>
    public int EditsUpdated { get; internal set; }

    /// <summary>Gets the number of earlier edits put back to stock because the plan no longer wants them.</summary>
    public int EditsReverted { get; internal set; }

    /// <summary>Gets the edits whose bytes matched neither the stock nor the converted bytes, which were left alone.</summary>
    public List<EditStatus> ForeignEdits { get; } = [];

    /// <summary>Gets the number of converted copies written this run.</summary>
    public int CopiesWritten { get; internal set; }

    /// <summary>Gets the number of converted copies already up to date.</summary>
    public int CopiesUpToDate { get; internal set; }

    /// <summary>Gets the number of converted copies deleted because the plan no longer wants them.</summary>
    public int CopiesDeleted { get; internal set; }

    /// <summary>Gets the bundles whose conversion failed, which are left out of the journal.</summary>
    public List<BundleProblem> Failures { get; } = [];

    /// <summary>Gets journal entries that were dropped because the bundle they describe has changed.</summary>
    public List<BundleProblem> DroppedEntries { get; } = [];
}

/// <summary>
/// The outcome of a revert.
/// </summary>
public sealed class RevertResult
{
    internal RevertResult(bool hadJournal)
    {
        HadJournal = hadJournal;
    }

    /// <summary>Gets a value indicating whether there was a journal to revert.</summary>
    public bool HadJournal { get; }

    /// <summary>Gets the number of edits put back to stock.</summary>
    public int EditsReverted { get; internal set; }

    /// <summary>Gets the number of edits that were already stock.</summary>
    public int EditsAlreadyStock { get; internal set; }

    /// <summary>Gets edits that could not be reverted because the file no longer holds the converted bytes.</summary>
    public List<EditStatus> Unreverted { get; } = [];

    /// <summary>Gets the number of converted copies deleted.</summary>
    public int CopiesDeleted { get; internal set; }

    /// <summary>Gets a value indicating whether the journal was deleted.</summary>
    public bool JournalDeleted { get; internal set; }

    /// <summary>Gets the failures that kept the journal in place.</summary>
    public List<BundleProblem> Failures { get; } = [];
}

/// <summary>
/// The outcome of a verification.
/// </summary>
public sealed class VerifyResult
{
    internal VerifyResult(bool hasJournal, string? manifestSha256)
    {
        HasJournal = hasJournal;
        ManifestSha256 = manifestSha256;
    }

    /// <summary>Gets a value indicating whether there is a journal.</summary>
    public bool HasJournal { get; }

    /// <summary>Gets the manifest hash the journal records.</summary>
    public string? ManifestSha256 { get; }

    /// <summary>Gets the state of every journaled in-place edit.</summary>
    public List<EditStatus> Edits { get; } = [];

    /// <summary>Gets the state of every journaled converted copy.</summary>
    public List<OutputStatus> Outputs { get; } = [];

    /// <summary>Gets a value indicating whether every edit is applied and every copy is present and intact.</summary>
    public bool IsComplete => HasJournal
                              && Edits.All(edit => edit.State == EditState.Applied)
                              && Outputs.All(output => output.State == OutputState.Ok);

    /// <summary>Gets the number of edits in a state.</summary>
    /// <param name="state">The state to count.</param>
    /// <returns>The number of edits.</returns>
    public int Count(EditState state) => Edits.Count(edit => edit.State == state);

    /// <summary>Gets the number of copies in a state.</summary>
    /// <param name="state">The state to count.</param>
    /// <returns>The number of copies.</returns>
    public int Count(OutputState state) => Outputs.Count(output => output.State == state);
}

/// <summary>
/// Settings for a conversion, revert or verification.
/// </summary>
public sealed class BundleConversionOptions
{
    /// <summary>
    /// Gets or sets how many bundles are read or written at once.
    /// </summary>
    public int MaxParallelism { get; set; } = 4;

    /// <summary>
    /// Gets or sets where progress lines go, or null for nowhere.
    /// </summary>
    public IProgress<string>? Progress { get; set; }
}
