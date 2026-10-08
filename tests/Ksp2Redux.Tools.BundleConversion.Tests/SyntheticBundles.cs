using System.Buffers.Binary;
using System.Text;
using AssetsTools.NET;
using Ksp2Redux.Tools.BundleConversion.Bundles;

namespace Ksp2Redux.Tools.BundleConversion.Tests;

/// <summary>
/// Builds small UnityFS bundles with hand-written type trees: just enough Material, Shader,
/// AssetBundle, MonoBehaviour and MonoScript fields for the converter to find what it looks for.
/// </summary>
internal static class SyntheticBundles
{
    public const string UNITY_VERSION = "2022.3.5f1";

    public static readonly TypeTreeType MATERIAL = Type(
        UnityClassId.MATERIAL,
        [
            new Node(0, "Material", "Base", -1),
            .. StringField(1, "m_Name"),
            .. PPtrField(1, "PPtr<Shader>", "m_Shader"),
            new Node(1, "float", "m_Tail", 4),
        ]);

    public static readonly TypeTreeType SHADER = Type(
        UnityClassId.SHADER,
        [
            new Node(0, "Shader", "Base", -1),
            .. StringField(1, "m_Name"),
            new Node(1, "SerializedShader", "m_ParsedForm", -1),
            .. StringField(2, "m_Name"),
        ]);

    public static readonly TypeTreeType ASSET_BUNDLE = Type(
        UnityClassId.ASSET_BUNDLE,
        [
            new Node(0, "AssetBundle", "Base", -1),
            .. StringField(1, "m_Name"),
            new Node(1, "vector", "m_PreloadTable", -1),
            new Node(2, "Array", "Array", -1, IsArray: true),
            new Node(3, "int", "size", 4),
            .. PPtrField(3, "PPtr<Object>", "data"),
        ]);

    public static readonly TypeTreeType MONO_BEHAVIOUR = Type(
        UnityClassId.MONO_BEHAVIOUR,
        [
            new Node(0, "MonoBehaviour", "Base", -1),
            .. PPtrField(1, "PPtr<GameObject>", "m_GameObject"),
            new Node(1, "UInt8", "m_Enabled", 1, Align: true),
            .. PPtrField(1, "PPtr<MonoScript>", "m_Script"),
            .. StringField(1, "m_Name"),
            .. PPtrField(1, "PPtr<$Shader>", "blurShader"),
        ],
        scriptIndex: 0);

    public static readonly TypeTreeType MONO_SCRIPT = Type(
        UnityClassId.MONO_SCRIPT,
        [
            new Node(0, "MonoScript", "Base", -1),
            .. StringField(1, "m_Name"),
            .. StringField(1, "m_ClassName"),
            .. StringField(1, "m_Namespace"),
            .. StringField(1, "m_AssemblyName"),
        ]);

    public static byte[] Shader(string name) => new ObjectData().String("").String(name).ToArray();

    public static byte[] Material(string name, int fileId, long pathId) =>
        new ObjectData().String(name).PPtr(fileId, pathId).Float(1f).ToArray();

    public static byte[] AssetBundle(string name, params (int FileId, long PathId)[] preload)
    {
        var data = new ObjectData().String(name).Int(preload.Length);
        foreach (var (fileId, pathId) in preload)
        {
            data.PPtr(fileId, pathId);
        }

        return data.ToArray();
    }

    public static byte[] MonoBehaviour(long scriptPathId, int shaderFileId, long shaderPathId) =>
        new ObjectData().PPtr(0, 0).Byte(1).Align().PPtr(0, scriptPathId).String("").PPtr(shaderFileId, shaderPathId).ToArray();

    public static byte[] MonoScript(string nameSpace, string className) =>
        new ObjectData().String(className).String(className).String(nameSpace).String("Assembly-CSharp").ToArray();

    /// <summary>
    /// Builds a UnityFS bundle around serialized files and resource blobs.
    /// </summary>
    public static byte[] Bundle(bool compress, params (string Name, byte[] Data, bool Serialized)[] entries)
    {
        var header = new AssetBundleHeader
        {
            Signature = "UnityFS",
            Version = 8,
            GenerationVersion = "5.x.x",
            EngineVersion = UNITY_VERSION,
            FileStreamHeader = new AssetBundleFSHeader(),
        };

        List<KeyValuePair<AssetBundleDirectoryInfo, Stream>> list =
        [
            .. entries.Select(entry => new KeyValuePair<AssetBundleDirectoryInfo, Stream>(
                new AssetBundleDirectoryInfo { Name = entry.Name, Flags = entry.Serialized ? 4u : 0u },
                new MemoryStream(entry.Data))),
        ];

        using MemoryStream stream = new();
        BundleRewriter.WriteBundle(stream, header, list, compress);
        return stream.ToArray();
    }

    private static IEnumerable<Node> StringField(int level, string name) =>
    [
        new Node(level, "string", name, -1, Align: true),
        new Node(level + 1, "Array", "Array", -1, IsArray: true, Align: true),
        new Node(level + 2, "int", "size", 4),
        new Node(level + 2, "char", "data", 1),
    ];

    private static IEnumerable<Node> PPtrField(int level, string type, string name) =>
    [
        new Node(level, type, name, 12),
        new Node(level + 1, "int", "m_FileID", 4),
        new Node(level + 1, "SInt64", "m_PathID", 8),
    ];

    private static TypeTreeType Type(int classId, IReadOnlyList<Node> nodes, ushort scriptIndex = 0xFFFF)
    {
        MemoryStream strings = new();
        Dictionary<string, uint> offsets = [];

        uint Offset(string value)
        {
            if (!offsets.TryGetValue(value, out var offset))
            {
                offset = (uint)strings.Length;
                strings.Write(Encoding.UTF8.GetBytes(value));
                strings.WriteByte(0);
                offsets[value] = offset;
            }

            return offset;
        }

        List<TypeTreeNode> list = [];
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            list.Add(new TypeTreeNode
            {
                Version = 1,
                Level = (byte)node.Level,
                TypeFlags = node.IsArray ? TypeTreeNodeFlags.Array : TypeTreeNodeFlags.None,
                TypeStrOffset = Offset(node.Type),
                NameStrOffset = Offset(node.Name),
                ByteSize = node.ByteSize,
                Index = (uint)index,
                MetaFlags = node.Align ? 0x4000u : 0u,
            });
        }

        return new TypeTreeType
        {
            TypeId = classId,
            IsStrippedType = false,
            ScriptTypeIndex = scriptIndex,
            ScriptIdHash = new Hash128(new byte[16]),
            TypeHash = new Hash128(new byte[16]),
            TypeBlob = new TypeTreeBlob { Nodes = list, StringBufferBytes = strings.ToArray() },
            TypeDependencies = [],
        };
    }

    /// <summary>
    /// One type tree node: its depth, type, name, declared size, and the array and align flags.
    /// </summary>
    internal sealed record Node(int Level, string Type, string Name, int ByteSize, bool IsArray = false, bool Align = false);

    /// <summary>
    /// Writes object data the way Unity lays it out: little-endian, strings padded to four bytes.
    /// </summary>
    internal sealed class ObjectData
    {
        private readonly MemoryStream _stream = new();

        public ObjectData String(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Int(bytes.Length);
            _stream.Write(bytes);
            return Align();
        }

        public ObjectData Int(int value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            _stream.Write(buffer);
            return this;
        }

        public ObjectData Long(long value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            _stream.Write(buffer);
            return this;
        }

        public ObjectData Float(float value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
            _stream.Write(buffer);
            return this;
        }

        public ObjectData Byte(byte value)
        {
            _stream.WriteByte(value);
            return this;
        }

        public ObjectData PPtr(int fileId, long pathId) => Int(fileId).Long(pathId);

        public ObjectData Align()
        {
            while (_stream.Length % 4 != 0)
            {
                _stream.WriteByte(0);
            }

            return this;
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}

/// <summary>
/// Builds one serialized file (CAB) for a synthetic bundle.
/// </summary>
internal sealed class SerializedFileBuilder
{
    private readonly List<string> _externals = [];
    private readonly List<(long PathId, TypeTreeType Type, byte[] Data)> _objects = [];
    private readonly List<AssetPPtr> _scripts = [];

    public SerializedFileBuilder(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public SerializedFileBuilder External(string cab)
    {
        _externals.Add(cab);
        return this;
    }

    public SerializedFileBuilder Object(long pathId, TypeTreeType type, byte[] data)
    {
        _objects.Add((pathId, type, data));
        return this;
    }

    public SerializedFileBuilder Script(int fileId, long pathId)
    {
        _scripts.Add(new AssetPPtr(fileId, pathId));
        return this;
    }

    public byte[] Build()
    {
        List<TypeTreeType> types = [.. _objects.Select(entry => entry.Type).Distinct()];
        List<AssetFileInfo> infos = [];
        foreach (var (pathId, type, data) in _objects)
        {
            var info = new AssetFileInfo
            {
                PathId = pathId,
                TypeIdOrIndex = types.IndexOf(type),
                ByteSize = (uint)data.Length,
            };
            info.SetNewData(data);
            infos.Add(info);
        }

        var file = new AssetsFile
        {
            Header = new AssetsFileHeader { Version = 22, Endianness = false },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = SyntheticBundles.UNITY_VERSION,
                TargetPlatform = 19,
                TypeTreeEnabled = true,
                TypeTreeTypes = types,
                AssetInfos = infos,
                ScriptTypes = _scripts,
                Externals =
                [
                    .. _externals.Select(cab => new AssetsFileExternal
                    {
                        VirtualAssetPathName = "",
                        Guid = default,
                        Type = AssetsFileExternalType.Normal,
                        PathName = $"archive:/{cab}/{cab}",
                        OriginalPathName = $"archive:/{cab}/{cab}",
                    }),
                ],
                RefTypes = [],
                UserInformation = "",
            },
        };

        using MemoryStream stream = new();
        file.Write(new AssetsFileWriter(stream));
        return stream.ToArray();
    }
}
