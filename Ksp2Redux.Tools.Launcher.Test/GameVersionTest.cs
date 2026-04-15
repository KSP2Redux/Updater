using Ksp2Redux.Tools.Launcher.Models;
using Mono.Cecil;

namespace Ksp2Redux.Tools.Launcher.Test;

public class GameVersionTest
{
    [Test]
    public void FromVersionIDType_CorrectVersionStock_CorrectGameVersionStableChannel(
        [Values("0.0.0.0", "1.2.3.4", "10.20.30.40")]
        string version,
        [Values("0", "12350")]
        string buildNumber
    )
    {
        // Arrange
        TypeDefinition versionIDType = GenerateMockVersionID(
            ("VERSION_TEXT", version + "." + buildNumber),
            ("DEBUG_INFO", "0000")
        );

        // Act
        GameVersion result = GameVersion.FromVersionIDType(versionIDType, false);
        
        // Assert
        Version expectedVersion = Version.Parse(version);
        string expectedBuildNumber = buildNumber;
        
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Channel, Is.EqualTo("stable"));
        Assert.That(result.VersionNumber, Is.EqualTo(expectedVersion));
        Assert.That(result.BuildNumber, Is.EqualTo(expectedBuildNumber));
    }

    [Test]
    public void FromVersionIDType_CorrectVersionRedux_CorrectGameVersionAndChannel(
        [Values("0.0.0.0", "1.2.3.4", "10.20.30.40")]
        string version,
        [Values("0", "12350")]
        string buildNumber,
        [Values("stable", "beta")]
        string channel
    )
    {
        // Arrange
        TypeDefinition versionIDType = GenerateMockVersionID(
            ("VERSION_TEXT", version + "." + buildNumber),
            ("CHANNEL_NAME", channel),
            ("DEBUG_INFO", "0000")
        );

        // Act
        GameVersion result = GameVersion.FromVersionIDType(versionIDType, true);
        
        // Assert
        Version expectedVersion = Version.Parse(version);
        string expectedBuildNumber = buildNumber;
        
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Channel, Is.EqualTo(channel));
        Assert.That(result.VersionNumber, Is.EqualTo(expectedVersion));
        Assert.That(result.BuildNumber, Is.EqualTo(expectedBuildNumber));
    }

    [Test]
    public void FromVersionIDType_NoVersionTextField_ThrowsInvalidOperation(
        [Values(true, false)]
        bool isRedux
    )
    {
        // Arrange
        TypeDefinition versionIDType = GenerateMockVersionID(
            ("CHANNEL_NAME", "stable"),
            ("DEBUG_INFO", "0000")
        );

        // Act
        Assert.Throws<InvalidOperationException>(() =>
        {
            GameVersion.FromVersionIDType(versionIDType, isRedux);
        });
    }

    [Test]
    public void FromVersionIDType_NullOrWhiteSpaceVersionText_ThrowsInvalidOperation(
        [Values("", " ")]
        string fullVersionText,
        [Values(true, false)]
        bool isRedux
    )
    {
        // Arrange
        TypeDefinition versionIDType = GenerateMockVersionID(
            ("VERSION_TEXT", fullVersionText),
            ("CHANNEL_NAME", "stable"),
            ("DEBUG_INFO", "0000")
        );

        // Act
        Assert.Throws<InvalidOperationException>(() =>
        {
            GameVersion.FromVersionIDType(versionIDType, isRedux);
        });
    }
    
    [Test]
    public void FromVersionIDType_IncorrectVersionParse_ThrowsInvalidOperation(
        [Values("a.b.c.d.e", "0.0.0..0", ".0.0.0.0")]
        string fullVersionText,
        [Values(true, false)]
        bool isRedux
    )
    {
        // Arrange
        TypeDefinition versionIDType = GenerateMockVersionID(
            ("VERSION_TEXT", fullVersionText),
            ("CHANNEL_NAME", "stable"),
            ("DEBUG_INFO", "0000")
        );

        // Act
        Assert.Throws<InvalidOperationException>(() =>
        {
            GameVersion.FromVersionIDType(versionIDType, isRedux);
        });
    }
    
    [Test]
    public void FromVersionIDType_IncorrectVersionTokenLength_ThrowsInvalidOperation(
        [Values("0.0.0.0", "0.0.0.0.0.0")]
        string fullVersionText,
        [Values(true, false)]
        bool isRedux
    )
    {
        // Arrange
        TypeDefinition versionIDType = GenerateMockVersionID(
            ("VERSION_TEXT", fullVersionText),
            ("CHANNEL_NAME", "stable"),
            ("DEBUG_INFO", "0000")
        );

        // Act
        Assert.Throws<InvalidOperationException>(() =>
        {
            GameVersion.FromVersionIDType(versionIDType, isRedux);
        });
    }
    
    [Test]
    public void FromVersionIDType_ReduxNoChannelNameField_ThrowsInvalidOperation()
    {
        // Arrange
        TypeDefinition versionIDType = GenerateMockVersionID(
            ("VERSION_TEXT", "0.0.0.0.0"),
            ("DEBUG_INFO", "0000")
        );

        // Act
        Assert.Throws<InvalidOperationException>(() =>
        {
            GameVersion.FromVersionIDType(versionIDType, true);
        });
    }

    [Test]
    public void FromVersionIDType_StockNoChannelNameField_StableChannel()
    {
        // Arrange
        TypeDefinition versionIDType = GenerateMockVersionID(
            ("VERSION_TEXT", "0.0.0.0.0"),
            ("DEBUG_INFO", "0000")
        );

        // Act
        GameVersion result = GameVersion.FromVersionIDType(versionIDType, true);
        
        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Channel, Is.EqualTo("stable"));
    }

    [Test]
    public void FromVersionIDType_ReduxNullChannelName_ThrowsInvalidOperation()
    {
        // Arrange
        TypeDefinition versionIDType = GenerateMockVersionID(
            ("VERSION_TEXT", "0.0.0.0.0"),
            ("CHANNEL_NAME", null),
            ("DEBUG_INFO", "0000")
        );

        // Act
        Assert.Throws<InvalidOperationException>(() =>
        {
            GameVersion.FromVersionIDType(versionIDType, true);
        });
    }

    [Test]
    public void FromVersionIDType_ReduxWhiteSpaceChannelName_ThrowsInvalidOperation(
        [Values("", " ")]
        string channelName
    )
    {
        // Arrange
        TypeDefinition versionIDType = GenerateMockVersionID(
            ("VERSION_TEXT", "0.0.0.0.0"),
            ("CHANNEL_NAME", channelName),
            ("DEBUG_INFO", "0000")
        );

        // Act
        Assert.Throws<InvalidOperationException>(() =>
        {
            GameVersion.FromVersionIDType(versionIDType, true);
        });
    }

    [Test]
    public void FromVersionIDType_NoDebugInfoField_ThrowsInvalidOperation(
        [Values(true, false)]
        bool isRedux
    )
    {
        // Arrange
        TypeDefinition versionIDType = GenerateMockVersionID(
            ("VERSION_TEXT", "0.0.0.0.0"),
            ("CHANNEL_NAME", "stable")
        );

        // Act
        Assert.Throws<InvalidOperationException>(() =>
        {
            GameVersion.FromVersionIDType(versionIDType, isRedux);
        });
    }

    [Test]
    public void FromVersionIDType_NullDebugInfo_ThrowsInvalidOperation(
        [Values(true, false)]
        bool isRedux
    )
    {
        // Arrange
        TypeDefinition versionIDType = GenerateMockVersionID(
            ("VERSION_TEXT", "0.0.0.0.0"),
            ("CHANNEL_NAME", "stable"),
            ("DEBUG_INFO", null)
        );

        // Act
        Assert.Throws<InvalidOperationException>(() =>
        {
            GameVersion.FromVersionIDType(versionIDType, isRedux);
        });
    }
    
    [Test]
    public void FromVersionIDType_WhiteSpaceDebugInfo_ThrowsInvalidOperation(
        [Values("", " ")]
        string debugInfo,
        [Values(true, false)]
        bool isRedux
    )
    {
        // Arrange
        TypeDefinition versionIDType = GenerateMockVersionID(
            ("VERSION_TEXT", "0.0.0.0.0"),
            ("CHANNEL_NAME", "stable"),
            ("DEBUG_INFO", debugInfo)
        );

        // Act
        Assert.Throws<InvalidOperationException>(() =>
        {
            GameVersion.FromVersionIDType(versionIDType, isRedux);
        });
    }
    
    [Test]
    public void FromVersionIDType_DebugInfoIsBuildInfo_EmptyCommitHash(
        // In theory this can only happen on stock game, but it should have the same behavior
        [Values(true, false)]
        bool isRedux
    )
    {
        // Arrange
        TypeDefinition versionIDType = GenerateMockVersionID(
            ("VERSION_TEXT", "0.0.0.0.0"),
            ("CHANNEL_NAME", "_"),
            ("DEBUG_INFO", "BUILD_INFO")
        );

        // Act
        GameVersion result = GameVersion.FromVersionIDType(versionIDType, isRedux);
        
        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.CommitHash, Is.EqualTo(string.Empty));
    }

    [Test]
    public void Equals_Null_ReturnsFalse()
    {
        // Arrange
        GameVersion gameVersion = new()
        {
            VersionNumber = Version.Parse("0.0.0.0"),
            BuildNumber = "1234",
        };
        
        // Act
        bool result = gameVersion.Equals(null);
        
        // Arrange
        Assert.That(result, Is.False);
    }

    [Test]
    public void Equals_OnlySameVersionNumber_ReturnsFalse()
    {
        // Arrange
        GameVersion gameVersion1 = new()
        {
            Channel = "a",
            VersionNumber = Version.Parse("0.0.0.0"),
            BuildNumber = "1234",
            CommitHash = "a"
        };
        GameVersion gameVersion2 = new()
        {
            Channel = "b",
            VersionNumber = Version.Parse("0.0.0.0"),
            BuildNumber = "5678",
            CommitHash = "b"
        };
        
        // Act
        bool result = gameVersion1.Equals(gameVersion2);
        
        // Arrange
        Assert.That(result, Is.False);
    }

    [Test]
    public void Equals_OnlySameBuildNumber_ReturnsFalse()
    {
        // Arrange
        GameVersion gameVersion1 = new()
        {
            Channel = "a",
            VersionNumber = Version.Parse("0.0.0.0"),
            BuildNumber = "1234",
            CommitHash = "a"
        };
        GameVersion gameVersion2 = new()
        {
            Channel = "b",
            VersionNumber = Version.Parse("1.2.3.4"),
            BuildNumber = "1234",
            CommitHash = "b"
        };
        
        // Act
        bool result = gameVersion1.Equals(gameVersion2);
        
        // Arrange
        Assert.That(result, Is.False);
    }

    [Test]
    public void Equals_OnlySameVersionNumberAndBuildNumber_ReturnsTrue()
    {
        // Arrange
        GameVersion gameVersion1 = new()
        {
            Channel = "a",
            VersionNumber = Version.Parse("0.0.0.0"),
            BuildNumber = "1234",
            CommitHash = "a"
        };
        GameVersion gameVersion2 = new()
        {
            Channel = "b",
            VersionNumber = Version.Parse("0.0.0.0"),
            BuildNumber = "1234",
            CommitHash = "b"
        };
        
        // Act
        bool result = gameVersion1.Equals(gameVersion2);
        
        // Arrange
        Assert.That(result, Is.True);
    }

    [Test]
    public void GetHashCode_SameBuildNumber_SameHash()
    {
        // Arrange
        GameVersion gameVersion1 = new()
        {
            Channel = "a",
            VersionNumber = Version.Parse("0.0.0.0"),
            BuildNumber = "1234",
            CommitHash = "a"
        };
        GameVersion gameVersion2 = new()
        {
            Channel = "b",
            VersionNumber = Version.Parse("1.2.3.4"),
            BuildNumber = "1234",
            CommitHash = "b"
        };
        
        // Act
        int hash1 = gameVersion1.GetHashCode();
        int hash2 = gameVersion2.GetHashCode();
        
        // Arrange
        Assert.That(hash1, Is.EqualTo(hash2));
    }

    [Test]
    public void GetHashCode_DifferentBuildNumber_DifferentHashes()
    {
        // Arrange
        GameVersion gameVersion1 = new()
        {
            Channel = "a",
            VersionNumber = Version.Parse("0.0.0.0"),
            BuildNumber = "1234",
            CommitHash = "a"
        };
        GameVersion gameVersion2 = new()
        {
            Channel = "b",
            VersionNumber = Version.Parse("1.2.3.4"),
            BuildNumber = "5678",
            CommitHash = "b"
        };
        
        // Act
        int hash1 = gameVersion1.GetHashCode();
        int hash2 = gameVersion2.GetHashCode();
        
        // Arrange
        Assert.That(hash1, Is.Not.EqualTo(hash2));
    }
    
    
    
    private static TypeDefinition GenerateMockVersionID(params List<(string name, string? value)> fields)
    {
        AssemblyDefinition? assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("MockAssembly", new Version(1, 0, 0, 0)),
            "MockModule", ModuleKind.Dll);
        ModuleDefinition? module = assembly.MainModule;

        TypeDefinition versionIDType = new(
            "_", "MockVersionID",
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);

        foreach (var fieldData in fields)
        {
            FieldDefinition field = new(
                fieldData.name,
                FieldAttributes.Public |
                FieldAttributes.Static |
                FieldAttributes.Literal |
                FieldAttributes.HasDefault,
                module.TypeSystem.String)
            {
                Constant = fieldData.value
            };
            versionIDType.Fields.Add(field);
        }

        module.Types.Add(versionIDType);
        return versionIDType;
    }
}
