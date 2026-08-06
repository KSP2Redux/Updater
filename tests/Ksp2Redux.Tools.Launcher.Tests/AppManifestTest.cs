namespace Ksp2Redux.Tools.Launcher.Tests;

public class AppManifestTest
{
    private static string FindAppManifestPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "src", "Ksp2Redux.Tools.Launcher", "app.manifest");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("Could not locate Ksp2Redux.Tools.Launcher/app.manifest by walking up from the test output directory.");
    }

    [Test]
    public void AppManifest_DeclaresLongPathAware()
    {
        var content = File.ReadAllText(FindAppManifestPath());

        Assert.That(content, Does.Contain("longPathAware"));
    }
}
