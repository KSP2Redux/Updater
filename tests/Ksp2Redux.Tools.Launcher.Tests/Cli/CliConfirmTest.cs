using Ksp2Redux.Tools.Cli.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class CliConfirmTest
{
    private static CliOutput NonInteractive() => new(new StringWriter(), isJson: false);

    [Test]
    public void Ask_AssumeYes_Approves()
    {
        // Act
        ConfirmAnswer answer = CliConfirm.Ask(NonInteractive(), assumeYes: true, "Remove it?", requireAnswer: true);

        // Assert
        Assert.That(answer, Is.EqualTo(ConfirmAnswer.Approved));
    }

    // uninstall has always run unattended, so a script that never had a terminal keeps working.
    [Test]
    public void Ask_NoTerminalAndAnswerNotRequired_Approves()
    {
        // Act
        ConfirmAnswer answer = CliConfirm.Ask(NonInteractive(), assumeYes: false, "Remove it?", requireAnswer: false);

        // Assert
        Assert.That(answer, Is.EqualTo(ConfirmAnswer.Approved));
    }

    // A command that never ran unattended before refuses to guess the first time it is asked to.
    [Test]
    public void Ask_NoTerminalAndAnswerRequired_AsksForTheFlag()
    {
        // Act
        ConfirmAnswer answer = CliConfirm.Ask(NonInteractive(), assumeYes: false, "Remove it?", requireAnswer: true);

        // Assert
        Assert.That(answer, Is.EqualTo(ConfirmAnswer.NeedsFlag));
    }
}
