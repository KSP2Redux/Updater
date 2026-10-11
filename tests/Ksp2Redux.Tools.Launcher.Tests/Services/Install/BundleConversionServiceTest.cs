using Ksp2Redux.Tools.BundleConversion;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

/// <summary>
/// A Redux build from before the URP port draws converted bundles magenta, so installing one over a converted
/// install has to put the stock bundles back.
/// </summary>
public class BundleConversionServiceTest
{
    private const string InstallDir = @"C:\Games\Ksp2";
    private const string BundleName = "plain.bundle";
    private const long EditOffset = 16;

    private static readonly byte[] StockBytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
    private static readonly byte[] ConvertedBytes = [21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32];

    [Test]
    public void ConvertForABuildWithoutShaderBundles_PutsTheConvertedBundlesBackToStock()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        var bundleDirectory = fs.Path.Combine(InstallDir, BundleConversionLayout.BUNDLE_ROOT.Replace('/', '\\'));
        fs.Directory.CreateDirectory(bundleDirectory);
        var bundle = new byte[64];
        ConvertedBytes.CopyTo(bundle, EditOffset);
        var bundlePath = fs.Path.Combine(bundleDirectory, BundleName);
        fs.File.WriteAllBytes(bundlePath, bundle);

        var journalPath = fs.Path.Combine(InstallDir, BundleConversionLayout.CONVERSION_ROOT.Replace('/', '\\'),
            BundleConversionLayout.JOURNAL_FILE);
        var journal = new ConversionJournal();
        journal.InPlace.Add(new JournalInPlaceFile
        {
            File = BundleName,
            Size = bundle.Length,
            Edits =
            [
                new JournalEdit
                {
                    Offset = EditOffset,
                    Old = Convert.ToHexString(StockBytes).ToLowerInvariant(),
                    New = Convert.ToHexString(ConvertedBytes).ToLowerInvariant(),
                },
            ],
        });
        new JournalStore(fs, journalPath).Write(journal);

        // The installed build ships no Redux/ShaderBundles manifest.
        new BundleConversionService(fs).Convert(InstallDir, _ => { }, CancellationToken.None);

        var expected = new byte[64];
        StockBytes.CopyTo(expected, EditOffset);
        Assert.That(fs.File.ReadAllBytes(bundlePath), Is.EqualTo(expected));
        Assert.That(fs.File.Exists(journalPath), Is.False);
    }
}