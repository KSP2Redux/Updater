using System.IO.Abstractions;
using AssetsTools.NET;

namespace Ksp2Redux.Tools.BundleConversion.Bundles;

/// <summary>
/// Unity class IDs the scanner treats specially.
/// </summary>
internal static class UnityClassId
{
    public const int MATERIAL = 21;
    public const int SHADER = 48;
    public const int MONO_BEHAVIOUR = 114;
    public const int MONO_SCRIPT = 115;
    public const int ASSET_BUNDLE = 142;
}

/// <summary>
/// Everything the planner needs from one bundle.
/// </summary>
internal sealed class ScannedBundle
{
    public ScannedBundle(string file, long size)
    {
        File = file;
        Size = size;
    }

    /// <summary>Gets the bundle file name.</summary>
    public string File { get; }

    /// <summary>Gets the bundle size in bytes.</summary>
    public long Size { get; }

    /// <summary>Gets or sets the block layout, or null when the bundle could not be opened.</summary>
    public BlockLayout? Layout { get; set; }

    /// <summary>Gets or sets why the bundle could not be read, or null when it was.</summary>
    public string? Error { get; set; }

    /// <summary>Gets the serialized files inside the bundle.</summary>
    public List<ScannedSerializedFile> Files { get; } = [];
}

/// <summary>
/// Everything the planner needs from one serialized file inside a bundle.
/// </summary>
internal sealed class ScannedSerializedFile
{
    public ScannedSerializedFile(string name, long dataOffset)
    {
        Name = name;
        DataOffset = dataOffset;
    }

    /// <summary>Gets the file name, the CAB-... name other files reference it by.</summary>
    public string Name { get; }

    /// <summary>Gets the offset of the file in the bundle's data stream.</summary>
    public long DataOffset { get; }

    /// <summary>Gets the externals, as CAB names for bundle files and raw paths otherwise. File ID n is entry n - 1.</summary>
    public List<string> Externals { get; } = [];

    /// <summary>Gets the shader names by path ID.</summary>
    public Dictionary<long, string> Shaders { get; } = [];

    /// <summary>Gets the MonoScript class names by path ID.</summary>
    public Dictionary<long, string> MonoScripts { get; } = [];

    /// <summary>Gets every serialized PPtr&lt;Shader&gt;.</summary>
    public List<ScannedShaderField> ShaderFields { get; } = [];

    /// <summary>Gets every AssetBundle preload table entry.</summary>
    public List<ScannedPreloadEntry> Preloads { get; } = [];

    /// <summary>Gets objects that could not be read.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>
    /// Resolves a file ID to the CAB or path it refers to.
    /// </summary>
    /// <param name="fileId">The file ID from a PPtr.</param>
    /// <returns>The CAB or path, or null when the ID is out of range.</returns>
    public string? ResolveFileId(int fileId)
    {
        if (fileId == 0)
        {
            return Name;
        }

        return fileId > 0 && fileId <= Externals.Count ? Externals[fileId - 1] : null;
    }
}

/// <summary>
/// A PPtr&lt;Shader&gt; field in an object.
/// </summary>
/// <param name="ObjectPathId">The path ID of the object holding the field.</param>
/// <param name="ClassId">The Unity class ID of that object.</param>
/// <param name="ObjectName">The object's m_Name, when it has one.</param>
/// <param name="ScriptFileId">For a MonoBehaviour, the file ID of its script.</param>
/// <param name="ScriptPathId">For a MonoBehaviour, the path ID of its script.</param>
/// <param name="FieldPath">The field path inside the object.</param>
/// <param name="DataOffset">The offset of the PPtr in the bundle's data stream.</param>
/// <param name="FileId">The file ID the PPtr holds.</param>
/// <param name="PathId">The path ID the PPtr holds.</param>
internal sealed record ScannedShaderField(
    long ObjectPathId,
    int ClassId,
    string? ObjectName,
    int ScriptFileId,
    long ScriptPathId,
    string FieldPath,
    long DataOffset,
    int FileId,
    long PathId);

/// <summary>
/// One entry of an AssetBundle object's preload table.
/// </summary>
/// <param name="AssetBundlePathId">The path ID of the AssetBundle object.</param>
/// <param name="FieldPath">The field path, m_PreloadTable[n].</param>
/// <param name="DataOffset">The offset of the PPtr in the bundle's data stream.</param>
/// <param name="FileId">The file ID the PPtr holds.</param>
/// <param name="PathId">The path ID the PPtr holds.</param>
internal sealed record ScannedPreloadEntry(long AssetBundlePathId, string FieldPath, long DataOffset, int FileId, long PathId);

/// <summary>
/// Reads the shaders, shader references and preload tables out of one bundle.
/// </summary>
internal static class BundleScanner
{
    private const uint SERIALIZED_FILE_FLAG = 4;
    private const ushort NO_SCRIPT = 0xFFFF;
    private const string PRELOAD_TABLE = "m_PreloadTable[";
    private const string ARCHIVE_PREFIX = "archive:/";

    private static readonly string[] SHADER_NAME_PATHS = ["m_ParsedForm.m_Name"];
    private static readonly string[] SCRIPT_NAME_PATHS = ["m_ClassName", "m_Namespace", "m_AssemblyName"];
    private static readonly string[] OBJECT_NAME_PATHS = ["m_Name"];

    /// <summary>
    /// Scans one bundle file.
    /// </summary>
    /// <param name="fileSystem">The file system to read through.</param>
    /// <param name="path">The bundle path.</param>
    /// <param name="overlay">File offsets and the bytes to read there instead, to see past earlier in-place edits.</param>
    /// <returns>What the bundle holds. A bundle that cannot be read comes back with <see cref="ScannedBundle.Error" /> set.</returns>
    public static ScannedBundle Scan(IFileSystem fileSystem, string path, IReadOnlyList<KeyValuePair<long, byte[]>> overlay)
    {
        var name = fileSystem.Path.GetFileName(path);
        Stream stream = fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        ScannedBundle result = new(name, stream.Length);

        try
        {
            if (overlay.Count > 0)
            {
                stream = new OverlayStream(stream, overlay);
            }

            using var bundle = UnityFsBundle.Open(stream);
            result.Layout = bundle.Layout;
            foreach (var entry in bundle.Directory)
            {
                if ((entry.Flags & SERIALIZED_FILE_FLAG) != 0)
                {
                    result.Files.Add(ScanSerializedFile(bundle, entry));
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // One unreadable bundle must not stop a conversion of the other three thousand. It is
            // reported and gets no edits.
            result.Error = e.Message;
            result.Files.Clear();
            stream.Dispose();
        }

        return result;
    }

    /// <summary>
    /// Turns an external path into the CAB name other files use for it.
    /// </summary>
    /// <param name="pathName">The external's path, such as archive:/CAB-x/CAB-x.</param>
    /// <returns>The CAB name for bundle files, otherwise the path unchanged.</returns>
    public static string ExternalName(string pathName)
    {
        if (!pathName.StartsWith(ARCHIVE_PREFIX, StringComparison.OrdinalIgnoreCase))
        {
            return pathName;
        }

        var slash = pathName.LastIndexOf('/');
        return pathName.Substring(slash + 1);
    }

    private static ScannedSerializedFile ScanSerializedFile(UnityFsBundle bundle, AssetBundleDirectoryInfo entry)
    {
        ScannedSerializedFile result = new(entry.Name, entry.Offset);
        var file = new AssetsFile();
        file.Read(new AssetsFileReader(new SegmentStream(bundle.Data, entry.Offset, entry.DecompressedSize)));

        var metadata = file.Metadata;
        foreach (var external in metadata.Externals)
        {
            result.Externals.Add(ExternalName(external.PathName));
        }

        if (file.Header.Endianness)
        {
            result.Warnings.Add("big-endian serialized file, not scanned");
            return result;
        }

        if (!metadata.TypeTreeEnabled)
        {
            result.Warnings.Add("serialized file has no type trees, not scanned");
            return result;
        }

        FileTypes types = new(metadata);
        foreach (var info in metadata.AssetInfos)
        {
            try
            {
                ScanObject(file, info, types, entry.Offset, result);
            }
            catch (Exception e) when (e is InvalidDataException or UnwalkableObjectException or EndOfStreamException)
            {
                result.Warnings.Add($"object {info.PathId} (class {info.TypeId}): {e.Message}");
            }
        }

        return result;
    }

    private static void ScanObject(AssetsFile file, AssetFileInfo info, FileTypes types, long fileOffset, ScannedSerializedFile result)
    {
        var classId = info.TypeId;
        var type = types.ForObject(info);
        if (type is null)
        {
            return;
        }

        var root = type.Value.Field;
        switch (classId)
        {
            case UnityClassId.SHADER:
            {
                var walker = Walk(file, info, root, WalkTargets.None, SHADER_NAME_PATHS, types);
                if (walker.Strings.TryGetValue(SHADER_NAME_PATHS[0], out var shaderName))
                {
                    result.Shaders[info.PathId] = shaderName;
                }

                return;
            }

            case UnityClassId.MONO_SCRIPT:
            {
                var walker = Walk(file, info, root, WalkTargets.None, SCRIPT_NAME_PATHS, types);
                walker.Strings.TryGetValue("m_ClassName", out var className);
                walker.Strings.TryGetValue("m_Namespace", out var nameSpace);
                walker.Strings.TryGetValue("m_AssemblyName", out var assembly);
                var qualified = string.IsNullOrEmpty(nameSpace) ? className : $"{nameSpace}.{className}";
                result.MonoScripts[info.PathId] = $"{qualified} [{assembly}]";
                return;
            }

            case UnityClassId.ASSET_BUNDLE:
            {
                var walker = Walk(file, info, root, WalkTargets.AllPPtrs, [], types);
                var objectOffset = fileOffset + info.GetAbsoluteByteOffset(file);
                foreach (var hit in walker.PPtrs)
                {
                    if (hit.Path.StartsWith(PRELOAD_TABLE, StringComparison.Ordinal))
                    {
                        result.Preloads.Add(new ScannedPreloadEntry(info.PathId, hit.Path, objectOffset + hit.Offset, hit.FileId, hit.PathId));
                    }
                }

                return;
            }
        }

        if (!root.HasShaderPPtr && !(root.HasRegistry && types.RefTypesHaveShaderPPtr))
        {
            return;
        }

        {
            var walker = Walk(file, info, root, WalkTargets.ShaderPPtrs, OBJECT_NAME_PATHS, types);
            if (walker.PPtrs.Count == 0)
            {
                return;
            }

            walker.Strings.TryGetValue("m_Name", out var objectName);
            var objectOffset = fileOffset + info.GetAbsoluteByteOffset(file);
            foreach (var hit in walker.PPtrs)
            {
                result.ShaderFields.Add(new ScannedShaderField(
                    info.PathId,
                    classId,
                    string.IsNullOrEmpty(objectName) ? null : objectName,
                    type.Value.ScriptFileId,
                    type.Value.ScriptPathId,
                    hit.Path,
                    objectOffset + hit.Offset,
                    hit.FileId,
                    hit.PathId));
            }
        }
    }

    private static ObjectWalker Walk(
        AssetsFile file,
        AssetFileInfo info,
        TypeField root,
        WalkTargets targets,
        IReadOnlyList<string> capturePaths,
        FileTypes types)
    {
        file.Reader.Position = info.GetAbsoluteByteOffset(file);
        var bytes = file.Reader.ReadBytes((int)info.ByteSize);
        if (bytes.Length != info.ByteSize)
        {
            throw new EndOfStreamException($"Object {info.PathId} is cut short: {bytes.Length} of {info.ByteSize} bytes.");
        }

        ObjectWalker walker = new(bytes, targets, capturePaths, types.FindRefType, types.RefTypesHaveShaderPPtr);
        walker.Walk(root);
        return walker;
    }

    /// <summary>
    /// The compiled type trees of one serialized file, built on first use.
    /// </summary>
    private sealed class FileTypes
    {
        private readonly AssetsFileMetadata _metadata;
        private readonly Dictionary<int, CompiledType?> _types = [];
        private readonly Dictionary<string, TypeTreeType> _refTypeTrees = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TypeField> _refTypes = new(StringComparer.Ordinal);
        private bool? _refTypesHaveShaderPPtr;

        public FileTypes(AssetsFileMetadata metadata)
        {
            _metadata = metadata;
            foreach (var refType in metadata.RefTypes ?? [])
            {
                var reference = refType.TypeReference;
                if (reference is not null)
                {
                    _refTypeTrees[Key(reference.ClassName, reference.Namespace, reference.AsmName)] = refType;
                }
            }
        }

        // Only a file whose SerializeReference types can hold a shader needs its registries walked.
        public bool RefTypesHaveShaderPPtr => _refTypesHaveShaderPPtr ??= _refTypeTrees.Keys.Any(key =>
        {
            var parts = key.Split('\n');
            return FindRefType(parts[2], parts[1], parts[0])?.HasShaderPPtr == true;
        });

        public CompiledType? ForObject(AssetFileInfo info)
        {
            if (_types.TryGetValue(info.TypeIdOrIndex, out var cached))
            {
                return cached;
            }

            CompiledType? compiled = null;
            if (info.TypeIdOrIndex >= 0 && info.TypeIdOrIndex < _metadata.TypeTreeTypes.Count)
            {
                var type = _metadata.TypeTreeTypes[info.TypeIdOrIndex];
                var scriptFileId = 0;
                long scriptPathId = 0;
                if (type.ScriptTypeIndex != NO_SCRIPT && type.ScriptTypeIndex < _metadata.ScriptTypes.Count)
                {
                    scriptFileId = _metadata.ScriptTypes[type.ScriptTypeIndex].FileId;
                    scriptPathId = _metadata.ScriptTypes[type.ScriptTypeIndex].PathId;
                }

                if (type.Nodes is { Count: > 0 })
                {
                    try
                    {
                        compiled = new CompiledType(TypeField.Compile(type), scriptFileId, scriptPathId);
                    }
                    catch (InvalidDataException)
                    {
                        // Remembered as unreadable so the warning comes once per type, not once per object.
                        _types[info.TypeIdOrIndex] = null;
                        throw;
                    }
                }
            }

            _types[info.TypeIdOrIndex] = compiled;
            return compiled;
        }

        public TypeField? FindRefType(string className, string nameSpace, string assembly)
        {
            var key = Key(className, nameSpace, assembly);
            if (_refTypes.TryGetValue(key, out var compiled))
            {
                return compiled;
            }

            if (!_refTypeTrees.TryGetValue(key, out var tree) || tree.Nodes is not { Count: > 0 })
            {
                return null;
            }

            compiled = TypeField.Compile(tree);
            _refTypes[key] = compiled;
            return compiled;
        }

        private static string Key(string? className, string? nameSpace, string? assembly) =>
            $"{assembly}\n{nameSpace}\n{className}";
    }

    /// <summary>
    /// A compiled object type and, for MonoBehaviours, the script it belongs to.
    /// </summary>
    private readonly struct CompiledType
    {
        public CompiledType(TypeField field, int scriptFileId, long scriptPathId)
        {
            Field = field;
            ScriptFileId = scriptFileId;
            ScriptPathId = scriptPathId;
        }

        public TypeField Field { get; }

        public int ScriptFileId { get; }

        public long ScriptPathId { get; }
    }
}
