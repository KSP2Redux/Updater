using Ksp2Redux.Tools.Cli.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class CliCommandCatalogTest
{
    // Completion is generated from the catalog, so a command that never made it in there is a
    // command the shell will never offer.
    [Test]
    public void Commands_EveryCommandInTheAssembly_HasACatalogEntry()
    {
        // Arrange
        Type[] settingsTypes =
        [
            .. typeof(CliCommandCatalog).Assembly
                .GetTypes()
                .Select(SettingsTypeOf)
                .Where(type => type is not null)
                .Select(type => type!)
                .Distinct()
        ];

        // Act
        Type[] missing = [.. settingsTypes.Where(type => CliCommandCatalog.Commands.All(c => c.SettingsType != type))];

        // Assert
        Assert.That(settingsTypes, Is.Not.Empty);
        Assert.That(missing, Is.Empty, $"missing from the catalog: {string.Join(", ", missing.Select(t => t.Name))}");
    }

    [Test]
    public void Candidates_NothingTyped_OffersEveryTopLevelCommand()
    {
        // Act
        IReadOnlyList<string> candidates = CliCommandCatalog.Candidates([], "");

        // Assert
        Assert.That(candidates, Does.Contain("install"));
        Assert.That(candidates, Does.Contain("installs"));
        Assert.That(candidates, Does.Contain("list-installs"));
        Assert.That(candidates, Does.Contain("version"));
        Assert.That(candidates, Does.Not.Contain("add"));
    }

    [Test]
    public void Candidates_PartialWord_OffersOnlyThatPrefix()
    {
        // Act
        IReadOnlyList<string> candidates = CliCommandCatalog.Candidates([], "inst");

        // Assert
        Assert.That(candidates, Is.EqualTo(new[] { "install", "installs" }));
    }

    [Test]
    public void Candidates_InsideABranch_OffersTheSubcommands()
    {
        // Act
        IReadOnlyList<string> candidates = CliCommandCatalog.Candidates(["installs"], "");

        // Assert
        Assert.That(candidates, Is.EqualTo(new[] { "add", "remove", "rename", "set-channel", "use" }));
    }

    // The alias is what a script or a habit types, and it has to complete like the real name.
    [Test]
    public void Candidates_AfterAnAlias_ResolvesToTheRealCommand()
    {
        // Act
        IReadOnlyList<string> candidates = CliCommandCatalog.Candidates(["list-installs"], "");

        // Assert
        Assert.That(candidates, Does.Contain("add"));
    }

    [Test]
    public void Candidates_DashPrefix_OffersTheCommandsOwnOptionsAndTheSharedOnes()
    {
        // Act
        IReadOnlyList<string> candidates = CliCommandCatalog.Candidates(["cache", "clear"], "--");

        // Assert
        Assert.That(candidates, Does.Contain("--older-than"));
        Assert.That(candidates, Does.Contain("--yes"));
        Assert.That(candidates, Does.Contain("--json"));
        Assert.That(candidates, Does.Contain("--quiet"));
    }

    [Test]
    public void Options_ACommandWithNoOptionsOfItsOwn_StillOffersTheSharedOnes()
    {
        // Act
        IReadOnlyList<string> options = CliCommandCatalog.Options("channels");

        // Assert
        Assert.That(options, Does.Contain("--json"));
        Assert.That(options, Does.Contain("--verbose"));
        Assert.That(options, Does.Contain("--color"));
    }

    private static Type? SettingsTypeOf(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ReduxCommand<>))
            {
                return current.GetGenericArguments()[0];
            }
        }

        return null;
    }
}
