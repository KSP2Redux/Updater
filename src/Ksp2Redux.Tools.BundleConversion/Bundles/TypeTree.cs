using System.Buffers.Binary;
using System.Text;
using AssetsTools.NET;

namespace Ksp2Redux.Tools.BundleConversion.Bundles;

/// <summary>
/// How a type tree node is laid out in object data.
/// </summary>
internal enum FieldKind
{
    /// <summary>A fixed-size leaf such as an int or a float.</summary>
    Primitive,

    /// <summary>A node whose children are stored one after another.</summary>
    Struct,

    /// <summary>An int32 element count followed by that many elements.</summary>
    Array,

    /// <summary>An int32 byte count followed by UTF-8 bytes.</summary>
    String,

    /// <summary>An int32 file ID followed by an int64 path ID.</summary>
    PPtr,

    /// <summary>The SerializeReference registry, whose element layouts live in the file's ref types.</summary>
    Registry,
}

/// <summary>
/// A type tree node compiled into a tree, with what its subtree contains worked out up front.
/// </summary>
internal sealed class TypeField
{
    /// <summary>
    /// The type name of a PPtr to a shader in native types such as Material.
    /// </summary>
    public const string SHADER_PPTR = "PPtr<Shader>";

    /// <summary>
    /// The type name of a PPtr to a shader in MonoBehaviour type trees, which mark script-visible
    /// object references with a dollar sign.
    /// </summary>
    public const string SCRIPT_SHADER_PPTR = "PPtr<$Shader>";

    private const uint ALIGN_FLAG = 0x4000;

    private TypeField(string type, string name, int byteSize, bool align, FieldKind kind, TypeField[] children)
    {
        Type = type;
        Name = name;
        ByteSize = byteSize;
        Align = align;
        Kind = kind;
        Children = children;

        IsShaderPPtr = kind == FieldKind.PPtr && (type == SHADER_PPTR || type == SCRIPT_SHADER_PPTR);
        HasShaderPPtr = IsShaderPPtr || children.Any(child => child.HasShaderPPtr);
        HasPPtr = kind == FieldKind.PPtr || children.Any(child => child.HasPPtr);
        HasRegistry = kind == FieldKind.Registry || children.Any(child => child.HasRegistry);
        FixedSize = ComputeFixedSize();
    }

    /// <summary>Gets the type name, such as PPtr&lt;Shader&gt; or vector.</summary>
    public string Type { get; }

    /// <summary>Gets the field name.</summary>
    public string Name { get; }

    /// <summary>Gets the byte size the type tree declares, or -1 for variable-size nodes.</summary>
    public int ByteSize { get; }

    /// <summary>Gets a value indicating whether the data is padded to four bytes after this node.</summary>
    public bool Align { get; }

    /// <summary>Gets how the node is laid out.</summary>
    public FieldKind Kind { get; }

    /// <summary>Gets the child nodes. An array's are its size and its element.</summary>
    public TypeField[] Children { get; }

    /// <summary>Gets a value indicating whether this node is a PPtr to a shader.</summary>
    public bool IsShaderPPtr { get; }

    /// <summary>Gets a value indicating whether the subtree holds a PPtr to a shader.</summary>
    public bool HasShaderPPtr { get; }

    /// <summary>Gets a value indicating whether the subtree holds any PPtr.</summary>
    public bool HasPPtr { get; }

    /// <summary>Gets a value indicating whether the subtree holds a SerializeReference registry.</summary>
    public bool HasRegistry { get; }

    /// <summary>
    /// Gets the subtree's size when it is the same for every object and has no padding inside it, otherwise -1.
    /// </summary>
    // Padding depends on where a value starts, so only subtrees without it can be skipped as one block.
    public int FixedSize { get; }

    /// <summary>
    /// Compiles a type tree into nodes.
    /// </summary>
    /// <param name="type">The type tree from a serialized file's metadata.</param>
    /// <returns>The root node, which describes the whole object.</returns>
    /// <exception cref="InvalidDataException">The type has no nodes.</exception>
    public static TypeField Compile(TypeTreeType type)
    {
        var nodes = type.Nodes;
        if (nodes is null || nodes.Count == 0)
        {
            throw new InvalidDataException($"Type {type.TypeId} has no type tree nodes.");
        }

        var strings = type.StringBufferBytes ?? [];
        var index = 0;
        return Build(nodes, strings, ref index);
    }

    private static TypeField Build(List<TypeTreeNode> nodes, byte[] strings, ref int index)
    {
        var node = nodes[index];
        index++;

        List<TypeField> children = [];
        while (index < nodes.Count && nodes[index].Level > node.Level)
        {
            children.Add(Build(nodes, strings, ref index));
        }

        var type = node.GetTypeString(strings);
        var name = node.GetNameString(strings);
        var align = (node.MetaFlags & ALIGN_FLAG) != 0;
        var kind = KindOf(node, type, children);
        return new TypeField(type, name, node.ByteSize, align, kind, [.. children]);
    }

    private static FieldKind KindOf(TypeTreeNode node, string type, List<TypeField> children)
    {
        if ((node.TypeFlags & TypeTreeNodeFlags.Array) != 0 || (type == "TypelessData" && children.Count == 2))
        {
            if (children.Count != 2)
            {
                throw new InvalidDataException($"Array node '{type}' has {children.Count} children, expected size and data.");
            }

            return FieldKind.Array;
        }

        if (type == "string" && children.Count == 1 && children[0].Kind == FieldKind.Array)
        {
            return FieldKind.String;
        }

        if (type.StartsWith("PPtr<", StringComparison.Ordinal) && children.Count == 2)
        {
            return FieldKind.PPtr;
        }

        if (type == "ManagedReferencesRegistry")
        {
            return FieldKind.Registry;
        }

        return children.Count == 0 ? FieldKind.Primitive : FieldKind.Struct;
    }

    private int ComputeFixedSize()
    {
        if (Align)
        {
            return -1;
        }

        switch (Kind)
        {
            case FieldKind.Primitive:
                return ByteSize >= 0 ? ByteSize : -1;
            case FieldKind.PPtr:
                return Hex.PPTR_SIZE;
            case FieldKind.Struct:
                var total = 0;
                foreach (var child in Children)
                {
                    if (child.FixedSize < 0)
                    {
                        return -1;
                    }

                    total += child.FixedSize;
                }

                return total;
            default:
                return -1;
        }
    }
}

/// <summary>
/// A PPtr found in an object, with where it sits in the object's bytes.
/// </summary>
/// <param name="Path">The field path, such as m_Shader or m_PreloadTable[3].</param>
/// <param name="Type">The PPtr type name, such as PPtr&lt;Shader&gt;.</param>
/// <param name="Offset">The offset of the 12 PPtr bytes from the start of the object.</param>
/// <param name="FileId">The file ID the PPtr holds.</param>
/// <param name="PathId">The path ID the PPtr holds.</param>
internal sealed record PPtrHit(string Path, string Type, int Offset, int FileId, long PathId);

/// <summary>
/// What an object walk reports.
/// </summary>
[Flags]
internal enum WalkTargets
{
    /// <summary>Nothing but the captured strings.</summary>
    None = 0,

    /// <summary>Every PPtr&lt;Shader&gt;.</summary>
    ShaderPPtrs = 1,

    /// <summary>Every PPtr of any type.</summary>
    AllPPtrs = 2,
}

/// <summary>
/// Thrown when an object cannot be walked, for example because a SerializeReference type is missing.
/// </summary>
internal sealed class UnwalkableObjectException : Exception
{
    /// <summary>
    /// Initializes the exception.
    /// </summary>
    /// <param name="message">What stopped the walk.</param>
    public UnwalkableObjectException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Walks one object's bytes along its type tree and reports PPtrs and chosen strings with their offsets.
/// </summary>
// AssetsTools.NET can deserialize the same data, but its value fields do not say where in the
// object each value came from, and an in-place edit needs exactly that.
internal sealed class ObjectWalker
{
    private const string TERMINUS_CLASS = "Terminus";
    private const string TERMINUS_NAMESPACE = "UnityEngine.DMAT";
    private const string TERMINUS_ASSEMBLY = "FAKE_ASM";

    private readonly byte[] _data;
    private readonly WalkTargets _targets;
    private readonly Func<string, string, string, TypeField?>? _refTypes;
    private readonly string[][] _capturePaths;
    private readonly List<KeyValuePair<string?, long>> _path = [];
    private readonly bool _registryMayHaveTargets;
    private int _capturesLeft;
    private bool _stop;

    /// <summary>
    /// Initializes a walker over one object's bytes.
    /// </summary>
    /// <param name="data">The object bytes, starting at the object.</param>
    /// <param name="targets">The PPtrs to report.</param>
    /// <param name="capturePaths">Dotted paths of strings to capture, such as m_ParsedForm.m_Name.</param>
    /// <param name="refTypes">Looks up a SerializeReference type by class, namespace and assembly, or null when the file has none.</param>
    /// <param name="registryMayHaveTargets">True when some SerializeReference type could hold a target.</param>
    public ObjectWalker(
        byte[] data,
        WalkTargets targets,
        IReadOnlyList<string> capturePaths,
        Func<string, string, string, TypeField?>? refTypes,
        bool registryMayHaveTargets)
    {
        _data = data;
        _targets = targets;
        _refTypes = refTypes;
        _registryMayHaveTargets = registryMayHaveTargets;
        _capturePaths = [.. capturePaths.Select(path => path.Split('.'))];
        _capturesLeft = _capturePaths.Length;
    }

    /// <summary>
    /// Gets the PPtrs found.
    /// </summary>
    public List<PPtrHit> PPtrs { get; } = [];

    /// <summary>
    /// Gets the captured strings by their dotted path.
    /// </summary>
    public Dictionary<string, string> Strings { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Walks the object.
    /// </summary>
    /// <param name="root">The compiled type of the object.</param>
    /// <exception cref="InvalidDataException">The bytes do not fit the type tree.</exception>
    /// <exception cref="UnwalkableObjectException">A SerializeReference type the data needs is missing.</exception>
    // The root's fields are walked only up to the last one that can hold something wanted, because
    // the fields after it cannot change any offset already found.
    public void Walk(TypeField root)
    {
        var last = -1;
        for (var index = 0; index < root.Children.Length; index++)
        {
            if (IsWanted(root.Children[index], atRoot: true))
            {
                last = index;
            }
        }

        var position = 0;
        for (var index = 0; index <= last && !_stop; index++)
        {
            position = WalkField(root.Children[index], position, named: true);
        }
    }

    private bool IsWanted(TypeField field, bool atRoot)
    {
        if ((_targets & WalkTargets.AllPPtrs) != 0 && field.HasPPtr)
        {
            return true;
        }

        if ((_targets & WalkTargets.ShaderPPtrs) != 0 && field.HasShaderPPtr)
        {
            return true;
        }

        if (field.HasRegistry && _registryMayHaveTargets && _targets != WalkTargets.None)
        {
            return true;
        }

        return atRoot && _capturePaths.Any(path => path[0] == field.Name);
    }

    private int WalkField(TypeField field, int position, bool named)
    {
        // A vector's elements sit under a node named Array, which would only add noise to the path:
        // m_PreloadTable[3] rather than m_PreloadTable.Array[3].
        named &= !(field.Kind == FieldKind.Array && field.Name == "Array");
        if (named)
        {
            _path.Add(new KeyValuePair<string?, long>(field.Name, -1));
        }

        switch (field.Kind)
        {
            case FieldKind.Primitive:
                position = Advance(position, field.ByteSize);
                break;

            case FieldKind.PPtr:
                Require(position, Hex.PPTR_SIZE);
                if ((_targets & WalkTargets.AllPPtrs) != 0 ||
                    ((_targets & WalkTargets.ShaderPPtrs) != 0 && field.IsShaderPPtr))
                {
                    PPtrs.Add(new PPtrHit(
                        FormatPath(),
                        field.Type,
                        position,
                        BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(position)),
                        BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(position + 4))));
                }

                position += Hex.PPTR_SIZE;
                break;

            case FieldKind.String:
                position = WalkString(field, position);
                break;

            case FieldKind.Array:
                position = WalkArray(field, position);
                break;

            case FieldKind.Struct:
                foreach (var child in field.Children)
                {
                    position = WalkField(child, position, named: true);
                    if (_stop)
                    {
                        break;
                    }
                }

                break;

            case FieldKind.Registry:
                position = WalkRegistry(position);
                break;
        }

        if (field.Align)
        {
            position = AlignTo4(position);
        }

        if (named)
        {
            _path.RemoveAt(_path.Count - 1);
        }

        return position;
    }

    private int WalkString(TypeField field, int position)
    {
        var length = ReadInt32(position);
        position += 4;
        Require(position, length);

        if (_capturesLeft > 0 && IsCapturePath())
        {
            Strings[FormatPath()] = Encoding.UTF8.GetString(_data, position, length);
            _capturesLeft--;
            if (_capturesLeft == 0 && _targets == WalkTargets.None)
            {
                _stop = true;
            }
        }

        position += length;
        if (field.Children[0].Align)
        {
            position = AlignTo4(position);
        }

        return position;
    }

    private int WalkArray(TypeField field, int position)
    {
        var count = ReadInt32(position);
        position += 4;
        if (count < 0)
        {
            throw new InvalidDataException($"Negative element count {count} at {FormatPath()}.");
        }

        var element = field.Children[1];
        if (element.FixedSize >= 0 && !IsWanted(element, atRoot: false))
        {
            return Advance(position, checked((long)count * element.FixedSize));
        }

        for (var index = 0; index < count && !_stop; index++)
        {
            _path.Add(new KeyValuePair<string?, long>(null, index));
            position = WalkField(element, position, named: false);
            _path.RemoveAt(_path.Count - 1);
        }

        return position;
    }

    // SerializeReference data is a list of (id, type, data) entries, and each entry's data follows
    // the type tree the file stores for that type rather than anything in the object's own tree.
    private int WalkRegistry(int position)
    {
        var version = ReadInt32(position);
        position += 4;

        if (version == 1)
        {
            for (var index = 0; !_stop; index++)
            {
                position = ReadTypeReference(position, out var className, out var nameSpace, out var assembly);
                if (className == TERMINUS_CLASS && nameSpace == TERMINUS_NAMESPACE && assembly == TERMINUS_ASSEMBLY)
                {
                    break;
                }

                position = WalkReferencedData(position, index, className, nameSpace, assembly);
            }

            return position;
        }

        var count = ReadInt32(position);
        position += 4;
        for (var index = 0; index < count && !_stop; index++)
        {
            Require(position, 8);
            var rid = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(position));
            position += 8;
            position = ReadTypeReference(position, out var className, out var nameSpace, out var assembly);
            position = WalkReferencedData(position, rid, className, nameSpace, assembly);
        }

        return position;
    }

    private int WalkReferencedData(int position, long rid, string className, string nameSpace, string assembly)
    {
        if (className.Length == 0)
        {
            return position;
        }

        var type = _refTypes?.Invoke(className, nameSpace, assembly)
                   ?? throw new UnwalkableObjectException(
                       $"SerializeReference type {nameSpace}.{className} [{assembly}] has no type tree in the file.");

        _path.Add(new KeyValuePair<string?, long>("references", rid));
        foreach (var child in type.Children)
        {
            position = WalkField(child, position, named: true);
            if (_stop)
            {
                break;
            }
        }

        if (type.Align)
        {
            position = AlignTo4(position);
        }

        _path.RemoveAt(_path.Count - 1);
        return position;
    }

    private int ReadTypeReference(int position, out string className, out string nameSpace, out string assembly)
    {
        position = ReadAlignedString(position, out className);
        position = ReadAlignedString(position, out nameSpace);
        return ReadAlignedString(position, out assembly);
    }

    private int ReadAlignedString(int position, out string value)
    {
        var length = ReadInt32(position);
        position += 4;
        Require(position, length);
        value = Encoding.UTF8.GetString(_data, position, length);
        return AlignTo4(position + length);
    }

    private bool IsCapturePath()
    {
        foreach (var capture in _capturePaths)
        {
            if (capture.Length != _path.Count)
            {
                continue;
            }

            var match = true;
            for (var index = 0; index < capture.Length; index++)
            {
                if (_path[index].Key != capture[index])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private string FormatPath()
    {
        StringBuilder builder = new();
        foreach (var segment in _path)
        {
            if (segment.Key is null)
            {
                builder.Append('[').Append(segment.Value).Append(']');
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(segment.Key);
            if (segment.Value >= 0)
            {
                builder.Append('[').Append(segment.Value).Append(']');
            }
        }

        return builder.ToString();
    }

    private int ReadInt32(int position)
    {
        Require(position, 4);
        return BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(position));
    }

    private int Advance(int position, long count)
    {
        if (count < 0)
        {
            throw new InvalidDataException($"Negative size {count} at {FormatPath()}.");
        }

        Require(position, count);
        return (int)(position + count);
    }

    private void Require(int position, long count)
    {
        if (count < 0 || position + count > _data.Length)
        {
            throw new InvalidDataException(
                $"Object data ends at {_data.Length} bytes but {FormatPath()} needs {count} bytes at {position}.");
        }
    }

    private static int AlignTo4(int position) => (position + 3) & ~3;
}
