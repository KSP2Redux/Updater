using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Ksp2Redux.Tools.Launcher.ViewModels.Steam;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

// SteamKit fails a request that gets no answer in time by cancelling its task, which is not the player cancelling.
public class SteamTimeoutTest
{
    [AvaloniaTest]
    public async Task PasswordSignIn_SteamTimesOut_ShowsAnErrorInsteadOfWorkingForever()
    {
        var session = new Mock<ISteamSessionService>();
        session.Setup(s => s.SignInWithQrAsync(It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource().Task);
        session.Setup(s => s.SignInWithCredentialsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ISteamGuardPrompt>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException());
        var signIn = new SteamSignInViewModel(session.Object, Mock.Of<ILogService>(), LocalizationService.Default)
        {
            AccountName = "jeb",
            Password = "hunter2"
        };

        await signIn.SignInWithPassword();
        Dispatcher.UIThread.RunJobs();

        Assert.That(signIn.Stage, Is.EqualTo(SteamSignInStage.Choose));
        Assert.That(signIn.ErrorMessage, Is.Not.Null);
    }

    [AvaloniaTest]
    public async Task Download_SteamTimesOut_StaysOpenWithAnError()
    {
        var downloader = new Mock<ISteamDepotDownloader>();
        downloader.Setup(d => d.DownloadAsync(It.IsAny<uint>(), It.IsAny<string>(), It.IsAny<IProgress<SteamDownloadProgress>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException());
        var download = new SteamDownloadViewModel(@"C:\Games\KSP2", downloader.Object, Mock.Of<IKsp2InstallService>(),
            new MockFileSystem(), Mock.Of<ILogService>(), LocalizationService.Default);
        var closed = false;
        download.Completed += (_, _) => closed = true;

        await download.StartAsync();

        Assert.That(closed, Is.False);
        Assert.That(download.ErrorMessage, Is.Not.Null);
        Assert.That(download.IsRunning, Is.False);
    }
}
