using System.Diagnostics;
using System.IO.Abstractions;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Mac;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourceInstallationServiceTest
{
    // The existing stream factory seam needs a span-aware wrapper to stop inside the real digest loop.
    private sealed class ReadinessReadStream : FileSystemStream
    {
        private readonly Action _read;
        private readonly Action _disposed;

        /// <summary>
        /// Observe short reads of the real fixture stream and its disposal.
        /// </summary>
        /// <param name="input">The existing mock filesystem stream.</param>
        /// <param name="read">The callback after a nonempty read.</param>
        /// <param name="disposed">The callback after stream disposal.</param>
        public ReadinessReadStream(FileSystemStream input, Action read, Action disposed)
            : base(input, input.Name, false)
        {
            _read = read;
            _disposed = disposed;
        }

        /// <inheritdoc />
        public override int Read(Span<byte> buffer)
        {
            int count = base.Read(buffer[..Math.Min(buffer.Length, 4)]);
            if (count != 0)
                _read();
            return count;
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                if (disposing)
                    _disposed();
            }
        }
    }

    private static FullSourceInstallationService ReadinessService(Fixture fixture, string observedPath,
        Func<FileSystemStream, FileSystemStream> observe)
    {
        var streams = new Mock<IFileStreamFactory>();
        streams.Setup(factory => factory.New(It.IsAny<string>(), It.IsAny<FileMode>(),
                It.IsAny<FileAccess>(), It.IsAny<FileShare>()))
            .Returns((string path, FileMode mode, FileAccess access, FileShare share) =>
            {
                FileSystemStream input = fixture.Fs.FileStream.New(path, mode, access, share);
                if (path != observedPath || mode != FileMode.Open || access != FileAccess.Read)
                    return input;
                try
                {
                    return observe(input);
                }
                catch
                {
                    input.Dispose();
                    throw;
                }
            });
        var fs = new Mock<IFileSystem>();
        fs.SetupGet(system => system.File).Returns(fixture.Fs.File);
        fs.SetupGet(system => system.Directory).Returns(fixture.Fs.Directory);
        fs.SetupGet(system => system.FileInfo).Returns(fixture.Fs.FileInfo);
        fs.SetupGet(system => system.DirectoryInfo).Returns(fixture.Fs.DirectoryInfo);
        fs.SetupGet(system => system.Path).Returns(fixture.Fs.Path);
        fs.SetupGet(system => system.FileStream).Returns(streams.Object);
        return new FullSourceInstallationService(fs.Object, fixture.Configuration, new ZipFileService(fixture.Fs),
            fixture.Space, fixture.Preparation.Object, fixture.Os, Mock.Of<ILogService>(), fixture.GameProcesses.Object,
            new InstallFileLockService(fs.Object, fixture.Os),
            engine: new InstallTransactionEngine(fs.Object, fixture.Space.GetAvailableFreeSpace, validateFileStreams: _ => { }));
    }

    private static HomeTabViewModel ReadinessHome(Fixture fixture, FullSourceInstallationService coordinator,
        IInstallationStateService state)
    {
        var installs = new Ksp2InstallService(fixture.Configuration, fixture.Fs, Mock.Of<IModuleDefinitionService>(),
            Mock.Of<ILogService>(), state, coordinator);
        var feeds = new Mock<IReleasesFeedService>();
        feeds.SetupGet(service => service.ReleasesFeed).Returns(new Dictionary<string, ManifestReleasesFeed>());
        return new HomeTabViewModel(installs, fixture.Configuration, feeds.Object, Mock.Of<IInstallPlanService>(),
            Mock.Of<IUpdateService>(), fixture.Os, Mock.Of<IMessageBoxService>(), Mock.Of<IEnvironmentProvider>(),
            fixture.Fs, Mock.Of<ILogService>(), Mock.Of<IWineRuntimeService>(), fixture.GameProcesses.Object, state);
    }

    /// <summary>
    /// Launch leases require published owned bytes without repairing damage or changing installation authority.
    /// </summary>
    /// <param name="damage">The external alteration, or healthy for an unchanged tree.</param>
    /// <returns>The verification task.</returns>
    [TestCase("healthy")]
    [TestCase("same-length-preserved-times")]
    [TestCase("missing")]
    [TestCase("stock-overwrite")]
    public async Task Readiness_LeaseChecksOwnedBytesWithoutImplicitRepair(string damage)
    {
        Fixture fixture = Create();
        await Apply(fixture);
        string path = At(fixture.Fs, fixture.Install, BUNDLE);
        if (damage == "same-length-preserved-times")
        {
            byte[] bytes = fixture.Fs.File.ReadAllBytes(path);
            string before = Digest(bytes);
            DateTime created = fixture.Fs.File.GetCreationTimeUtc(path);
            DateTime written = fixture.Fs.File.GetLastWriteTimeUtc(path);
            DateTime accessed = fixture.Fs.File.GetLastAccessTimeUtc(path);
            bytes[0] ^= 1;
            fixture.Fs.File.WriteAllBytes(path, bytes);
            fixture.Fs.File.SetCreationTimeUtc(path, created);
            fixture.Fs.File.SetLastWriteTimeUtc(path, written);
            fixture.Fs.File.SetLastAccessTimeUtc(path, accessed);
            Assert.That(fixture.Fs.FileInfo.New(path).Length, Is.EqualTo(bytes.LongLength));
            Assert.That(Digest(bytes), Is.Not.EqualTo(before));
            Assert.That(fixture.Fs.File.GetCreationTimeUtc(path), Is.EqualTo(created));
            Assert.That(fixture.Fs.File.GetLastWriteTimeUtc(path), Is.EqualTo(written));
            Assert.That(fixture.Fs.File.GetLastAccessTimeUtc(path), Is.EqualTo(accessed));
        }
        else if (damage == "missing")
        {
            fixture.Fs.File.Delete(path);
        }
        else if (damage == "stock-overwrite")
        {
            fixture.Fs.File.WriteAllBytes(path, fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Stock.SourceRoot, BUNDLE)));
        }
        Dictionary<string, string> beforeLease = LiveUseSnapshot(fixture);
        var coordinator = fixture.Service();
        if (damage == "healthy")
        {
            using (IDisposable? lease = coordinator.AcquireLaunchLease(fixture.Install))
            {
                Assert.That(lease, Is.Not.Null);
                Assert.Catch<IOException>(() => fixture.Service().AcquireLaunchLease(fixture.Install));
            }
            using (IDisposable? next = fixture.Service().AcquireLaunchLease(fixture.Install))
                Assert.That(next, Is.Not.Null);
        }
        else
        {
            if (damage == "missing")
                Assert.Catch<IOException>(() => coordinator.AcquireLaunchLease(fixture.Install));
            else
                Assert.Throws<InvalidDataException>(() => coordinator.AcquireLaunchLease(fixture.Install));
        }
        Assert.That(coordinator.GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(fixture.Target2.Identity));
        LiveUseUnchanged(fixture, beforeLease);
        fixture.GameProcesses.Verify(service => service.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
    }

    /// <summary>
    /// Pre-cancellation and cancellation inside the real digest release the root reservation and digest stream.
    /// </summary>
    /// <param name="duringDigest">True to cancel after the second short read, false to cancel before entry.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task Readiness_CancelledDigestReleasesReservation(bool duringDigest)
    {
        Fixture fixture = Create();
        await Apply(fixture);
        using var cancellation = new CancellationTokenSource();
        int reads = 0;
        bool disposed = false;
        var coordinator = ReadinessService(fixture, At(fixture.Fs, fixture.Install, BUNDLE),
            input => new ReadinessReadStream(input, () =>
            {
                if (++reads == 2)
                    cancellation.Cancel();
            }, () => disposed = true));
        if (!duringDigest)
            cancellation.Cancel();
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        Assert.Throws<OperationCanceledException>(() => coordinator.AcquireLaunchLease(fixture.Install, cancellation.Token));
        Assert.That(reads, Is.EqualTo(duringDigest ? 2 : 0));
        Assert.That(disposed, Is.EqualTo(duringDigest));
        using (IDisposable? next = fixture.Service().AcquireLaunchLease(fixture.Install))
            Assert.That(next, Is.Not.Null);
        Assert.That(coordinator.GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(fixture.Target2.Identity));
        LiveUseUnchanged(fixture, before);
        fixture.GameProcesses.Verify(service => service.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
    }

    /// <summary>
    /// Unreadable changed user files do not participate in owned-file validation or become owned.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task Readiness_ChangedUnreadableUnownedFileIsNotOpenedOrAdopted()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        const string relative = "mods/user-content.bin";
        Put(fixture.Fs, fixture.Install, relative, "first user revision");
        Put(fixture.Fs, fixture.Install, relative, "changed user revision");
        string path = At(fixture.Fs, fixture.Install, relative);
        int opened = 0;
        var coordinator = ReadinessService(fixture, path, _ =>
        {
            opened++;
            throw new UnauthorizedAccessException("Injected unreadable user content");
        });
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        using (IDisposable? lease = coordinator.AcquireLaunchLease(fixture.Install))
            Assert.That(lease, Is.Not.Null);
        Assert.That(opened, Is.Zero);
        FullSourceInstallationStatus status = coordinator.Reconcile(fixture.Entry.Id)!;
        Assert.That(status.Release, Is.EqualTo(fixture.Target2.Identity));
        Assert.That(status.Generation.Files.Any(file => file.Path == relative), Is.False);
        Assert.That(fixture.Fs.File.ReadAllText(path), Is.EqualTo("changed user revision"));
        LiveUseUnchanged(fixture, before);
        fixture.GameProcesses.Verify(service => service.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
    }

    /// <summary>
    /// The GUI can cancel actual worker-thread digest preparation before any process handoff.
    /// </summary>
    /// <returns>The verification task.</returns>
    [AvaloniaTest]
    public async Task Readiness_GuiCancelsWorkerDigestWithoutStartingGame()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        int uiThread = Thread.CurrentThread.ManagedThreadId;
        int readerThread = uiThread;
        int reads = 0;
        bool disposed = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var coordinator = ReadinessService(fixture, At(fixture.Fs, fixture.Install, BUNDLE),
            input => new ReadinessReadStream(input, () =>
            {
                if (++reads != 2)
                    return;
                readerThread = Thread.CurrentThread.ManagedThreadId;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("GUI did not release the controlled digest read.");
            }, () => disposed = true));
        var state = new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs), coordinator);
        var home = ReadinessHome(fixture, coordinator, state);
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        Task launch = home.LaunchGame();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(Dispatcher.UIThread.CheckAccess(), Is.True);
            Assert.That(readerThread, Is.Not.EqualTo(uiThread));
            Assert.That(launch.IsCompleted, Is.False);
            home.CancelCurrentMainButtonAction();
        }
        finally
        {
            home.CancelCurrentMainButtonAction();
            release.Set();
            await launch.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.That(reads, Is.EqualTo(2));
        Assert.That(disposed, Is.True);
        fixture.GameProcesses.Verify(service => service.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
        using (IDisposable? next = fixture.Service().AcquireLaunchLease(fixture.Install))
            Assert.That(next, Is.Not.Null);
        LiveUseUnchanged(fixture, before);
    }

    /// <summary>
    /// Cancellation after real lease acquisition disposes the lease before the GUI can start a process.
    /// </summary>
    /// <returns>The verification task.</returns>
    [AvaloniaTest]
    public async Task Readiness_GuiCancellationBeforeHandoffDisposesAcquiredLease()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        var coordinator = fixture.Service();
        var realState = new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs), coordinator);
        var state = new Mock<IInstallationStateService>(MockBehavior.Strict);
        state.Setup(service => service.Inspect(fixture.Install)).Returns(() => realState.Inspect(fixture.Install));
        state.Setup(service => service.EnsureLaunchAllowed(fixture.Install))
            .Callback(() => realState.EnsureLaunchAllowed(fixture.Install));
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        state.Setup(service => service.AcquireLaunchLease(fixture.Install, It.IsAny<CancellationToken>()))
            .Returns((string root, CancellationToken ct) =>
            {
                IDisposable? lease = realState.AcquireLaunchLease(root, ct);
                acquired.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    lease?.Dispose();
                    throw new TimeoutException("GUI did not release the acquired launch lease.");
                }
                return lease;
            });
        var home = ReadinessHome(fixture, coordinator, state.Object);
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        Task launch = home.LaunchGame();
        try
        {
            await acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(Dispatcher.UIThread.CheckAccess(), Is.True);
            Assert.That(launch.IsCompleted, Is.False);
            Assert.Catch<IOException>(() => fixture.Service().AcquireLaunchLease(fixture.Install));
            home.CancelCurrentMainButtonAction();
        }
        finally
        {
            home.CancelCurrentMainButtonAction();
            release.Set();
            await launch.WaitAsync(TimeSpan.FromSeconds(10));
        }
        fixture.GameProcesses.Verify(service => service.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
        using (IDisposable? next = fixture.Service().AcquireLaunchLease(fixture.Install))
            Assert.That(next, Is.Not.Null);
        LiveUseUnchanged(fixture, before);
    }
}
