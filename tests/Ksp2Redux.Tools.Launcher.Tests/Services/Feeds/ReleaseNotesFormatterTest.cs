using Ksp2Redux.Tools.Launcher.Services.Feeds;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Feeds;

public class ReleaseNotesFormatterTest
{
    // Exactly what GitHub generates, which is the whole body now.
    private const string REAL_BODY = """
        ## What's Changed
        * Game data folder button, kill command, shorter release notes by @EwyBoy in https://github.com/KSP2Redux/Updater/pull/64

        **Full Changelog**: https://github.com/KSP2Redux/Updater/compare/updater-v0.4.3.1...updater-v0.4.3.2
        """;

    [Test]
    public void ToPlainText_TheRealReleaseBody_ReadsAsText()
    {
        // Act
        string plain = ReleaseNotesFormatter.ToPlainText(REAL_BODY);

        // Assert
        Assert.That(plain, Does.StartWith("What's Changed"));
        Assert.That(plain, Does.Contain("Game data folder button, kill command"));
        Assert.That(plain, Does.Contain("in #64"), "the pull request URL is shortened to its number");
        Assert.That(plain, Does.Not.Contain("https://"), "no bare URLs survive");
    }

    // Underscore emphasis is left alone on purpose, see the file name test below.
    [Test]
    public void ToPlainText_Markup_IsStripped()
    {
        // Act
        string plain = ReleaseNotesFormatter.ToPlainText("## Heading\n**bold** and *italic* and `code`");

        // Assert
        Assert.That(plain, Is.EqualTo("Heading\nbold and italic and code"));
    }

    // A full pull request URL is most of a line in a narrow dialog, and the number is the useful part.
    [Test]
    public void ToPlainText_PullRequestLink_BecomesItsNumber()
    {
        // Act
        string plain = ReleaseNotesFormatter.ToPlainText("* Did a thing by @EwyBoy in https://github.com/KSP2Redux/Updater/pull/64");

        // Assert
        Assert.That(plain, Is.EqualTo("- Did a thing by @EwyBoy in #64"));
    }

    [Test]
    public void ToPlainText_MarkdownLink_KeepsTheTextAndDropsTheUrl()
    {
        // Act
        string plain = ReleaseNotesFormatter.ToPlainText("See the [readme](https://github.com/KSP2Redux/Updater#command-line) for more.");

        // Assert
        Assert.That(plain, Is.EqualTo("See the readme for more."));
    }

    [Test]
    public void ToPlainText_TableAndRule_AreDropped()
    {
        // Act
        string plain = ReleaseNotesFormatter.ToPlainText("Before\n\n---\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\nAfter");

        // Assert
        Assert.That(plain, Is.EqualTo("Before\n\nAfter"));
    }

    // Underscores are markdown emphasis, but in a release note they are far more likely to be part of
    // a file name. Stripping them would rename the file the note is talking about.
    [Test]
    public void ToPlainText_FileNameWithUnderscores_IsLeftAlone()
    {
        // Act
        string plain = ReleaseNotesFormatter.ToPlainText("Fixed reading KSP2_x64_Data on startup");

        // Assert
        Assert.That(plain, Is.EqualTo("Fixed reading KSP2_x64_Data on startup"));
    }

    // Whatever else ends up in a body, the dialog should get words rather than markup.
    [Test]
    public void ToPlainText_HeadingsAndRules_StillStripsMarkup()
    {
        // Act
        string plain = ReleaseNotesFormatter.ToPlainText("## What's Changed\n* A thing\n\n---\n\n### Notes\n\n**Bold** lead in.");

        // Assert
        Assert.That(plain, Does.Not.Contain("#"));
        Assert.That(plain, Does.Not.Contain("*"));
        Assert.That(plain, Does.Contain("Bold lead in."));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   \n  ")]
    public void ToPlainText_NothingToShow_IsEmpty(string? body)
    {
        // Act, Assert
        Assert.That(ReleaseNotesFormatter.ToPlainText(body), Is.Empty);
    }


}
