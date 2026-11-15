using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Steam;

public class SteamSessionServiceTest
{
    // On macOS every read of the store runs /usr/bin/security, which can wait for the keychain for seconds.
    [Test]
    public void SavedLogin_ReadRepeatedly_ReadsTheStoreOnce()
    {
        var store = new Mock<ISteamLoginStore>();
        store.Setup(s => s.Load()).Returns(new SavedSteamLogin("jeb", "token", null));
        var session = new SteamSessionService(store.Object, Mock.Of<ILogService>());

        for (var i = 0; i < 5; i++)
        {
            Assert.That(session.HasSavedLogin, Is.True);
            Assert.That(session.SavedAccountName, Is.EqualTo("jeb"));
        }

        store.Verify(s => s.Load(), Times.Once);
    }

    [Test]
    public async Task SignOut_WithNothingSaved_ForgetsTheLoginWithoutReadingTheStoreAgain()
    {
        var store = new Mock<ISteamLoginStore>();
        var session = new SteamSessionService(store.Object, Mock.Of<ILogService>());
        Assert.That(session.HasSavedLogin, Is.False);

        await session.SignOutAsync();

        Assert.That(session.HasSavedLogin, Is.False);
        Assert.That(await session.TryResumeAsync(CancellationToken.None), Is.False);
        store.Verify(s => s.Load(), Times.Once);
        store.Verify(s => s.Clear(), Times.Once);
    }
}
