using Ksp2Redux.Tools.BundleConversion.Bundles;
using Ksp2Redux.Tools.BundleConversion.Textures;

namespace Ksp2Redux.Tools.BundleConversion.Tests;

public class TextureOverlayTest
{
    private const string BODY = "body.bundle";
    private const string CAB = "CAB-00000000000000000000000000000c1d";
    private const string NORMAL = "body_scaledclouds_01_Normal";
    private const string DENSITY = "body_scaledclouds_01";
    private const string COLOR = "body_scaledclouds_02";
    private const string READABLE = "body_scaledclouds_top_normal";
    private const int WIDTH = 8;
    private const int MIPS = 4;
    private const int HALF_PIXELS = 85;
    private const int HALF_FACE = HALF_PIXELS * 8;
    private const int COLOR_FACE = WIDTH * WIDTH * 4;
    private const int NORMAL_OFFSET = 16;
    private const int DENSITY_OFFSET = NORMAL_OFFSET + (HALF_FACE * 6);
    private const int COLOR_OFFSET = DENSITY_OFFSET + (HALF_FACE * 6);

    private static readonly ShaderManifestIndex NO_SHADERS = TestGame.Manifest(Array.Empty<string>());

    [Test]
    public void Apply_CloudCubemapsInAnUncompressedBundle_OverlaysThemAndLeavesTheStockFileAlone()
    {
        // Arrange
        TestGame game = new();
        game.Write(BODY, CloudBundle(compress: false));
        var stock = game.Read(BODY);

        // Act
        var result = game.Converter().Apply(NO_SHADERS);

        // Assert
        Assert.That(result.Failures, Is.Empty);
        Assert.That(result.TextureFailures, Is.Empty);
        Assert.That(game.Read(BODY), Is.EqualTo(stock));
        var planned = result.Plan.TextureBundles.Single().Textures.ToDictionary(texture => texture.Name);
        Assert.That(planned.Keys, Is.EquivalentTo(new[] { NORMAL, COLOR, READABLE }));
        Assert.That(planned[NORMAL].TargetFormat, Is.EqualTo(CubemapRecompression.RGBA32));
        Assert.That(planned[COLOR].TargetFormat, Is.EqualTo(CubemapRecompression.BC7));
        Assert.That(planned[READABLE].ChangesFormat, Is.False);

        var overlay = game.Journal()!.Overlaid.Single();
        Assert.That(overlay.Ranges, Has.Count.EqualTo(2));
        Assert.That(overlay.DataSha256, Is.Not.Empty);
        var data = game.FileSystem.File.ReadAllBytes(game.Layout.Resolve(overlay.Data));
        Assert.That(data, Has.Length.EqualTo((HALF_FACE * 3) + (Bc7Encoder.EncodedSize(WIDTH, WIDTH) * 6)));
        Assert.That(result.OverlaysWritten, Is.EqualTo(1));
    }

    [Test]
    public void Apply_CloudCubemaps_TheOverlaidBundleHoldsTheNewHeadersAndPixels()
    {
        // Arrange
        TestGame game = new();
        game.Write(BODY, CloudBundle(compress: false));
        game.Converter().Apply(NO_SHADERS);
        var overlay = game.Journal()!.Overlaid.Single();
        var data = game.FileSystem.File.ReadAllBytes(game.Layout.Resolve(overlay.Data));

        // Act: read the bundle the way the game does, with the overlay laid over it.
        var view = Overlaid(game.Read(BODY), overlay, data);
        game.FileSystem.File.WriteAllBytes(@"C:\view.bundle", view);
        var scanned = BundleScanner.Scan(game.FileSystem, @"C:\view.bundle", []).Files.Single().Textures.ToDictionary(texture => texture.Name);

        // Assert
        var normal = scanned[NORMAL];
        Assert.That(normal.Value(ScannedTexture.FORMAT), Is.EqualTo(CubemapRecompression.RGBA32));
        Assert.That(normal.Value(ScannedTexture.COMPLETE_IMAGE_SIZE), Is.EqualTo(HALF_PIXELS * 4));
        Assert.That(normal.Value(ScannedTexture.STREAM_SIZE), Is.EqualTo(HALF_PIXELS * 4 * 6));
        var color = scanned[COLOR];
        Assert.That(color.Value(ScannedTexture.FORMAT), Is.EqualTo(CubemapRecompression.BC7));
        Assert.That(color.Value(ScannedTexture.STREAM_SIZE), Is.EqualTo(Bc7Encoder.EncodedSize(WIDTH, WIDTH) * 6));
        Assert.That(scanned[READABLE].Value(ScannedTexture.IS_READABLE), Is.Zero);
        Assert.That(scanned[DENSITY].Value(ScannedTexture.FORMAT), Is.EqualTo(CubemapRecompression.RGBA_HALF));

        // The normal map's halves come back as the bytes they were made from, face by face and mip by mip.
        var normalStart = (int)overlay.Ranges.First().Offset;
        var expected = Enumerable.Range(0, HALF_PIXELS * 4 * 6).Select(index => (byte)((index * 37) % 256)).ToArray();
        Assert.That(view.AsSpan(normalStart, expected.Length).ToArray(), Is.EqualTo(expected));

        // The color map's blocks decode close to its smooth source on every face.
        var colorStart = (int)overlay.Ranges.Last().Offset;
        for (var face = 0; face < 6; face++)
        {
            for (var blockIndex = 0; blockIndex < 4; blockIndex++)
            {
                var decoded = Bc7EncoderTest.DecodeMode6(view, colorStart + (((face * 4) + blockIndex) * Bc7Encoder.BLOCK_BYTES));
                for (var pixel = 0; pixel < 16; pixel++)
                {
                    var x = ((blockIndex & 1) * 4) + (pixel & 3);
                    var y = ((blockIndex >> 1) * 4) + (pixel >> 2);
                    for (var channel = 0; channel < 4; channel++)
                    {
                        Assert.That(decoded[(pixel * 4) + channel], Is.EqualTo(ColorValue(face, x, y, channel)).Within(4f));
                    }
                }
            }
        }
    }

    [Test]
    public void Apply_RunTwice_ReusesTheOverlay()
    {
        // Arrange
        TestGame game = new();
        game.Write(BODY, CloudBundle(compress: false));
        game.Converter().Apply(NO_SHADERS);
        var dataPath = game.Layout.Resolve(game.Journal()!.Overlaid.Single().Data);
        var data = game.FileSystem.File.ReadAllBytes(dataPath);

        // Act
        var second = game.Converter().Apply(NO_SHADERS);

        // Assert
        Assert.That(second.OverlaysWritten, Is.Zero);
        Assert.That(second.OverlaysUpToDate, Is.EqualTo(1));
        Assert.That(game.FileSystem.File.ReadAllBytes(dataPath), Is.EqualTo(data));
        Assert.That(game.Converter().Verify().IsComplete, Is.True);
    }

    [Test]
    public void Verify_OverlayDataDeleted_ReportsItMissing()
    {
        // Arrange
        TestGame game = new();
        game.Write(BODY, CloudBundle(compress: false));
        game.Converter().Apply(NO_SHADERS);
        var overlay = game.Journal()!.Overlaid.Single();
        game.FileSystem.File.Delete(game.Layout.Resolve(overlay.Data));

        // Act
        var result = game.Converter().Verify();

        // Assert
        Assert.That(result.IsComplete, Is.False);
        Assert.That(result.Outputs.Single(output => output.File == BODY).State, Is.EqualTo(OutputState.Missing));
    }

    [Test]
    public void Revert_AfterTextureOverlays_DeletesTheirDataAndTheJournal()
    {
        // Arrange
        TestGame game = new();
        game.Write(BODY, CloudBundle(compress: false));
        var stock = game.Read(BODY);
        game.Converter().Apply(NO_SHADERS);
        var dataPath = game.Layout.Resolve(game.Journal()!.Overlaid.Single().Data);

        // Act
        var result = game.Converter().Revert();

        // Assert
        Assert.That(result.OverlaysDeleted, Is.EqualTo(1));
        Assert.That(result.JournalDeleted, Is.True);
        Assert.That(game.FileSystem.File.Exists(dataPath), Is.False);
        Assert.That(game.Read(BODY), Is.EqualTo(stock));
    }

    [Test]
    public void Apply_CompressedBundle_LeavesTheCloudCubemapsAndSaysWhy()
    {
        // Arrange
        TestGame game = new();
        game.Write(BODY, CloudBundle(compress: true));

        // Act
        var result = game.Converter().Apply(NO_SHADERS);

        // Assert
        Assert.That(result.Plan.TextureBundles, Is.Empty);
        Assert.That(result.Plan.SkippedTextures.Select(skip => skip.Name), Is.EquivalentTo(new[] { NORMAL, COLOR, READABLE }));
        Assert.That(result.Plan.SkippedTextures.Select(skip => skip.Reason), Has.All.Contains("compressed blocks"));
        Assert.That(game.Journal()!.Overlaid, Is.Empty);
    }

    [Test]
    public void Apply_NormalMapValuesAboveOne_KeepsTheStockTexturesWithoutFailingTheConversion()
    {
        // Arrange: 8 bits cannot hold these, so the bundle keeps its stock textures.
        TestGame game = new();
        game.Write(BODY, CloudBundle(compress: false, normalScale: 1.5f));

        // Act
        var result = game.Converter().Apply(NO_SHADERS);

        // Assert
        Assert.That(result.Failures, Is.Empty);
        Assert.That(result.TextureFailures.Single().File, Is.EqualTo(BODY));
        Assert.That(game.Journal()!.Overlaid, Is.Empty);
        Assert.That(game.FileSystem.Directory.Exists(game.Layout.OverlayDirectory) &&
                    game.FileSystem.Directory.EnumerateFiles(game.Layout.OverlayDirectory).Any(), Is.False);
    }

    // A body bundle with a 16-bit normal map, a 16-bit density map, an 8-bit cloud map, all three in the resource
    // file, and a small readable normal map with its pixels inside the object.
    private static byte[] CloudBundle(bool compress, float normalScale = 1f)
    {
        var resource = new byte[COLOR_OFFSET + (COLOR_FACE * 6)];
        for (var index = 0; index < HALF_PIXELS * 4 * 6; index++)
        {
            WriteHalf(resource, NORMAL_OFFSET + (index * 2), (index * 37) % 256 / 255f * normalScale);
            WriteHalf(resource, DENSITY_OFFSET + (index * 2), index % 4 == 2 ? 640f : 0.64f);
        }

        for (var face = 0; face < 6; face++)
        {
            for (var pixel = 0; pixel < WIDTH * WIDTH; pixel++)
            {
                for (var channel = 0; channel < 4; channel++)
                {
                    resource[COLOR_OFFSET + (face * COLOR_FACE) + (pixel * 4) + channel] = (byte)ColorValue(face, pixel % WIDTH, pixel / WIDTH, channel);
                }
            }
        }

        var inline = new byte[4 * 4 * 8 * 6];
        for (var index = 0; index < inline.Length / 2; index++)
        {
            WriteHalf(inline, index * 2, 0.5f);
        }

        var resourcePath = $"archive:/{CAB}/{CAB}.resS";
        var file = new SerializedFileBuilder(CAB)
            .Object(40, SyntheticBundles.CUBEMAP, SyntheticBundles.Cubemap(NORMAL, WIDTH, CubemapRecompression.RGBA_HALF, MIPS, HALF_FACE, false, null, NORMAL_OFFSET, resourcePath))
            .Object(41, SyntheticBundles.CUBEMAP, SyntheticBundles.Cubemap(DENSITY, WIDTH, CubemapRecompression.RGBA_HALF, MIPS, HALF_FACE, false, null, DENSITY_OFFSET, resourcePath))
            .Object(42, SyntheticBundles.CUBEMAP, SyntheticBundles.Cubemap(COLOR, WIDTH, CubemapRecompression.RGBA32, 1, COLOR_FACE, false, null, COLOR_OFFSET, resourcePath))
            .Object(43, SyntheticBundles.CUBEMAP, SyntheticBundles.Cubemap(READABLE, 4, CubemapRecompression.RGBA_HALF, 1, 128, true, inline, 0, ""));

        return SyntheticBundles.Bundle(compress, (CAB, file.Build(), true), (CAB + ".resS", resource, false));
    }

    private static float ColorValue(int face, int x, int y, int channel) => (x * 8) + (y * 4) + (channel * 30) + (face * 5);

    private static void WriteHalf(byte[] buffer, int offset, float value)
    {
        var bits = BitConverter.HalfToUInt16Bits((Half)value);
        buffer[offset] = (byte)bits;
        buffer[offset + 1] = (byte)(bits >> 8);
    }

    private static byte[] Overlaid(byte[] stock, JournalOverlayFile overlay, byte[] data)
    {
        var view = (byte[])stock.Clone();
        foreach (var range in overlay.Ranges)
        {
            Array.Copy(data, range.DataOffset, view, range.Offset, range.Length);
        }

        foreach (var edit in overlay.Edits)
        {
            var bytes = Convert.FromHexString(edit.New);
            Array.Copy(bytes, 0, view, edit.Offset, bytes.Length);
        }

        return view;
    }
}
