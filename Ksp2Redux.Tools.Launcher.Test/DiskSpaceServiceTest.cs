using System.IO.Abstractions;
using Ksp2Redux.Tools.Launcher.Services;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Test;

public class DiskSpaceServiceTest
{
    private const long OneGb = 1024L * 1024 * 1024;

    private static (Mock<IFileSystem> Fs, Mock<IPath> Path, Mock<IDirectory> Directory, Mock<IDriveInfoFactory> Drives)
        MakeFileSystemMock()
    {
        var fs = new Mock<IFileSystem>();
        var path = new Mock<IPath>();
        var directory = new Mock<IDirectory>();
        var drives = new Mock<IDriveInfoFactory>();
        fs.SetupGet(f => f.Path).Returns(path.Object);
        fs.SetupGet(f => f.Directory).Returns(directory.Object);
        fs.SetupGet(f => f.DriveInfo).Returns(drives.Object);
        return (fs, path, directory, drives);
    }

    private static Mock<IDriveInfo> DriveWithFreeSpace(long bytes)
    {
        var drive = new Mock<IDriveInfo>();
        drive.SetupGet(d => d.AvailableFreeSpace).Returns(bytes);
        return drive;
    }

    [Test]
    public void GetAvailableFreeSpace_UnixPath_QueriesTheDirectoryItselfNotTheRoot()
    {
        // On SteamOS the rootfs ("/") is a tiny separate partition from /home, where games live.
        // Querying "/" reported ~1 GB free even though the install drive had >100 GB (issue seen
        // on Steam Deck). The service must hand the install directory itself to DriveInfo.
        const string installDir = "/home/deck/.local/share/Steam/steamapps/common/Kerbal Space Program 2";
        var (fs, path, directory, drives) = MakeFileSystemMock();
        path.Setup(p => p.GetFullPath(installDir)).Returns(installDir);
        path.Setup(p => p.GetPathRoot(installDir)).Returns("/");
        directory.Setup(d => d.Exists(installDir)).Returns(true);
        drives.Setup(f => f.New(installDir)).Returns(DriveWithFreeSpace(119 * OneGb).Object);

        var result = new DiskSpaceService(fs.Object).GetAvailableFreeSpace(installDir);

        Assert.That(result, Is.EqualTo(119 * OneGb));
        drives.Verify(f => f.New("/"), Times.Never);
    }

    [Test]
    public void GetAvailableFreeSpace_UnixPathDoesNotExist_WalksUpToDeepestExistingDirectory()
    {
        const string missingDir = "/home/deck/Games/NotThereYet";
        const string existingParent = "/home/deck/Games";
        var (fs, path, directory, drives) = MakeFileSystemMock();
        path.Setup(p => p.GetFullPath(missingDir)).Returns(missingDir);
        path.Setup(p => p.GetPathRoot(missingDir)).Returns("/");
        path.Setup(p => p.GetDirectoryName(missingDir)).Returns(existingParent);
        directory.Setup(d => d.Exists(missingDir)).Returns(false);
        directory.Setup(d => d.Exists(existingParent)).Returns(true);
        drives.Setup(f => f.New(existingParent)).Returns(DriveWithFreeSpace(50 * OneGb).Object);

        var result = new DiskSpaceService(fs.Object).GetAvailableFreeSpace(missingDir);

        Assert.That(result, Is.EqualTo(50 * OneGb));
    }

    [Test]
    public void GetAvailableFreeSpace_WindowsPath_QueriesTheDriveRoot()
    {
        const string installDir = @"C:\Games\Ksp2";
        const string root = @"C:\";
        var (fs, path, _, drives) = MakeFileSystemMock();
        path.Setup(p => p.GetFullPath(installDir)).Returns(installDir);
        path.Setup(p => p.GetPathRoot(installDir)).Returns(root);
        drives.Setup(f => f.New(root)).Returns(DriveWithFreeSpace(200 * OneGb).Object);

        var result = new DiskSpaceService(fs.Object).GetAvailableFreeSpace(installDir);

        Assert.That(result, Is.EqualTo(200 * OneGb));
    }

    [Test]
    public void GetAvailableFreeSpace_DriveQueryFails_ReturnsNull()
    {
        const string installDir = @"C:\Games\Ksp2";
        var (fs, path, _, drives) = MakeFileSystemMock();
        path.Setup(p => p.GetFullPath(installDir)).Returns(installDir);
        path.Setup(p => p.GetPathRoot(installDir)).Returns(@"C:\");
        drives.Setup(f => f.New(It.IsAny<string>())).Throws(new IOException("no such device"));

        var result = new DiskSpaceService(fs.Object).GetAvailableFreeSpace(installDir);

        Assert.That(result, Is.Null);
    }
}
