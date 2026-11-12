using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Ksp2Redux.Tools.BundleConversion;

/// <summary>
/// Shares verified finished shader bundles between editor checkouts and maintains their idle lifetime.
/// </summary>
/// <remarks>
/// The editor and command-line maintenance use the same storage, validation and per-entry leases.
/// Unity's incremental Library data stays under Unity's ownership.
/// </remarks>
public sealed class ShaderBundleCache
{
    private const string LAST_USED = ".last-used";
    private const int LOCK_EXCLUSIVE_NONBLOCKING = 2 | 4;
    private readonly TimeSpan _idleLifetime;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Gets the cache directory, independent of the current project directory.
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// Creates a finished-bundle cache.
    /// </summary>
    /// <param name="fileSystem">The filesystem providing storage and leases.</param>
    /// <param name="root">The cache directory.</param>
    /// <param name="idleDays">The number of idle days before an entry expires.</param>
    public ShaderBundleCache(IFileSystem fileSystem, string root, int idleDays = 14)
    {
        if (idleDays < 1)
            throw new ArgumentOutOfRangeException(nameof(idleDays));
        _fileSystem = fileSystem;
        Root = _fileSystem.Path.GetFullPath(root);
        _idleLifetime = TimeSpan.FromDays(idleDays);
        EnsureNoLinks(Root);
    }

    /// <summary>
    /// Gets the configured cache, or null when explicitly disabled.
    /// </summary>
    /// <param name="fileSystem">The filesystem providing storage and leases.</param>
    /// <returns>The shared cache, or null for an empty or off override.</returns>
    public static ShaderBundleCache? FromEnvironment(IFileSystem fileSystem)
    {
        string? configured = Environment.GetEnvironmentVariable("REDUX_SHADER_BUNDLE_CACHE");
        bool supplied = configured != null;
        if (supplied && (string.IsNullOrWhiteSpace(configured) || string.Equals(configured, "off", StringComparison.OrdinalIgnoreCase)))
            return null;

        int days = 14;
        string? retention = Environment.GetEnvironmentVariable("REDUX_SHADER_CACHE_IDLE_DAYS");
        if (!string.IsNullOrEmpty(retention) &&
            (!int.TryParse(retention, NumberStyles.None, CultureInfo.InvariantCulture, out days) || days < 1 || days > 3650))
            throw new ArgumentException("REDUX_SHADER_CACHE_IDLE_DAYS must be between 1 and 3650.");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string root;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            root = fileSystem.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ksp2Redux", "Cache", "ShaderBundles");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            root = fileSystem.Path.Combine(home, "Library", "Caches", "Ksp2Redux", "ShaderBundles");
        }
        else
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            string parent = !string.IsNullOrEmpty(xdg) && fileSystem.Path.IsPathRooted(xdg) ? xdg : fileSystem.Path.Combine(home, ".cache");
            root = fileSystem.Path.Combine(parent, "ksp2redux", "shader-bundles");
        }
        return new ShaderBundleCache(fileSystem, supplied ? configured! : root, days);
    }

    /// <summary>
    /// Restores a matching verified entry and records its use.
    /// </summary>
    /// <param name="key">The complete build-input hash.</param>
    /// <param name="destination">The checkout's published shader directory.</param>
    /// <returns>True if a verified entry was restored, false if absent, invalid or busy.</returns>
    public bool TryRestore(string key, string destination)
    {
        EnsureSeparateDirectory(destination);
        string entry = Entry(key);
        using Stream? lease = TryLease(key);
        if (lease == null || !TryRead(entry, key, out ShaderManifest manifest))
            return false;
        EnsureNoLinks(_fileSystem.Path.GetFullPath(destination));
        _fileSystem.Directory.CreateDirectory(destination);
        CopyVerifiedFiles(entry, destination, manifest);
        Touch(entry);
        return true;
    }

    /// <summary>
    /// Publishes verified checkout output atomically, or refreshes an existing matching entry.
    /// </summary>
    /// <param name="key">The complete build-input hash.</param>
    /// <param name="source">The checkout's published shader directory.</param>
    /// <returns>True if the entry was published or reused, false if another process holds it.</returns>
    public bool Publish(string key, string source)
    {
        EnsureSeparateDirectory(source);
        string entry = Entry(key);
        if (!TryRead(source, key, out ShaderManifest manifest))
            throw new InvalidDataException("Cannot cache unverified shader bundles.");
        using Stream? lease = TryLease(key);
        if (lease == null)
            return false;
        if (TryRead(entry, key, out _))
        {
            Touch(entry);
            return true;
        }

        string staging = entry + ".partial-" + Guid.NewGuid().ToString("N");
        _fileSystem.Directory.CreateDirectory(staging);
        try
        {
            CopyVerifiedFiles(source, staging, manifest);
            if (!TryRead(staging, key, out _))
                throw new InvalidDataException("Shader bundle output changed during cache publication.");
            Touch(staging);
            if (_fileSystem.Directory.Exists(entry))
            {
                EnsureRegularTree(entry);
                _fileSystem.Directory.Delete(entry, true);
            }
            _fileSystem.Directory.Move(staging, entry);
        }
        finally
        {
            if (_fileSystem.Directory.Exists(staging))
                _fileSystem.Directory.Delete(staging, true);
        }
        return true;
    }

    /// <summary>
    /// Removes expired managed entries and abandoned publications while respecting active leases.
    /// </summary>
    /// <param name="utcNow">The current UTC time, or null to read the clock.</param>
    /// <returns>The number of removed entries.</returns>
    public int Prune(DateTime? utcNow = null)
    {
        EnsureNoLinks(Root);
        if (!_fileSystem.Directory.Exists(Root))
            return 0;
        DateTime cutoff = (utcNow ?? DateTime.UtcNow) - _idleLifetime;
        int removed = 0;
        foreach (string entry in _fileSystem.Directory.EnumerateDirectories(Root))
        {
            try
            {
                if (PruneEntry(entry, cutoff))
                    removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    private bool PruneEntry(string entry, DateTime cutoff)
    {
        string name = _fileSystem.Path.GetFileName(entry);
        string key = name.Length >= 64 ? name.Substring(0, 64) : name;
        string prefix = key + ".partial-";
        bool partial = name == key + ".partial" ||
            (name.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(prefix.Length), "N", out _));
        if (!IsKey(key) || (!partial && name != key) || HasLink(entry))
            return false;
        using Stream? lease = TryLease(key);
        if (lease == null || !_fileSystem.Directory.Exists(entry))
            return false;
        string stamp = _fileSystem.Path.Combine(entry, LAST_USED);
        DateTime used = _fileSystem.File.Exists(stamp) && !HasLink(stamp)
            ? _fileSystem.File.GetLastWriteTimeUtc(stamp)
            : _fileSystem.File.GetLastWriteTimeUtc(entry);
        if (used >= cutoff || (!partial && !IsManagedEntry(entry, key)))
            return false;
        EnsureRegularTree(entry);
        _fileSystem.Directory.Delete(entry, true);
        return true;
    }

    private bool IsManagedEntry(string directory, string key)
    {
        string path = _fileSystem.Path.Combine(directory, "manifest.json");
        EnsureNoLinks(path);
        if (!_fileSystem.File.Exists(path))
            return false;
        try
        {
            byte[] bytes = _fileSystem.File.ReadAllBytes(path);
            int offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            ShaderManifest? manifest = JsonCodec.Read<ShaderManifest>(bytes.AsSpan(offset));
            return manifest != null && manifest.Version == 1 && manifest.SourceHash == key;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private string Entry(string key)
    {
        if (!IsKey(key))
            throw new ArgumentException("A shader cache key must be a lowercase SHA-256 hash.", nameof(key));
        EnsureNoLinks(Root);
        return _fileSystem.Path.Combine(Root, key);
    }

    private void EnsureSeparateDirectory(string path)
    {
        string other = _fileSystem.Path.GetFullPath(path).TrimEnd('/', '\\');
        string root = Root.TrimEnd('/', '\\');
        StringComparison comparison = _fileSystem.Path.DirectorySeparatorChar == '\\' || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        char separator = _fileSystem.Path.DirectorySeparatorChar;
        if (string.Equals(root, other, comparison) || root.StartsWith(other + separator, comparison) || other.StartsWith(root + separator, comparison))
            throw new IOException("The shared shader cache and checkout output must be separate directories.");
    }

    private static bool IsKey(string key) => key.Length == 64 && key.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f');

    private Stream? TryLease(string key)
    {
        string locks = _fileSystem.Path.Combine(Root, ".locks");
        EnsureNoLinks(locks);
        _fileSystem.Directory.CreateDirectory(locks);
        string path = _fileSystem.Path.Combine(locks, key + ".lock");
        EnsureNoLinks(path);
        Stream lease;
        try
        {
            lease = _fileSystem.FileStream.New(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return lease;

            // Mono's Unix FileShare.None does not coordinate with CoreCLR. Both runtimes use this flock protocol.
            SafeFileHandle handle = BorrowNativeHandle(lease);
            if (Flock(handle, LOCK_EXCLUSIVE_NONBLOCKING) == 0)
            {
                GC.KeepAlive(lease);
                return lease;
            }
            int error = Marshal.GetLastWin32Error();
            if (error is 11 or 35)
            {
                lease.Dispose();
                return null;
            }
            throw new IOException($"Cannot acquire shader cache lease (flock errno {error}).");
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle file, int operation);

    // All file creation stays behind IFileSystem. Native locking needs the borrowed handle of that factory's stream.
#pragma warning disable RS0030
    private static SafeFileHandle BorrowNativeHandle(Stream stream)
    {
#if UNITY_EDITOR
        return ((System.IO.FileStream)stream).SafeFileHandle;
#else
        return ((System.IO.FileStream)(FileSystemStream)stream).SafeFileHandle;
#endif
    }
#pragma warning restore RS0030

    private bool TryRead(string directory, string key, out ShaderManifest manifest)
    {
        manifest = null!;
        if (!_fileSystem.Directory.Exists(directory))
            return false;
        try
        {
            EnsureNoLinks(_fileSystem.Path.GetFullPath(directory));
            string stamp = _fileSystem.Path.Combine(directory, LAST_USED);
            EnsureNoLinks(stamp);
            if (_fileSystem.Directory.Exists(stamp))
                return false;
            string path = _fileSystem.Path.Combine(directory, "manifest.json");
            if (!_fileSystem.File.Exists(path) || HasLink(path))
                return false;
            byte[] bytes = _fileSystem.File.ReadAllBytes(path);
            int offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            manifest = JsonCodec.Read<ShaderManifest>(bytes.AsSpan(offset))!;
            if (manifest == null || manifest.Version != 1 || manifest.SourceHash != key || manifest.Bundles == null || manifest.Bundles.Count == 0)
                return false;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ShaderManifestBundle bundle in manifest.Bundles)
            {
                if (bundle == null)
                    return false;
                string? file = bundle.File;
                if (string.IsNullOrEmpty(file) || _fileSystem.Path.GetFileName(file) != file || file.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                    !file.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) || !names.Add(file) ||
                    !IsKey(bundle.Sha256?.ToLowerInvariant() ?? ""))
                    return false;
                string binary = _fileSystem.Path.Combine(directory, file);
                if (!_fileSystem.File.Exists(binary) || HasLink(binary))
                    return false;
                using var sha = SHA256.Create();
                using Stream stream = _fileSystem.FileStream.New(binary, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!string.Equals(Hex.Encode(sha.ComputeHash(stream)), bundle.Sha256, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private void CopyVerifiedFiles(string source, string destination, ShaderManifest manifest)
    {
        foreach (ShaderManifestBundle bundle in manifest.Bundles)
        {
            string path = _fileSystem.Path.Combine(destination, bundle.File!);
            EnsureNoLinks(path);
            _fileSystem.File.Copy(_fileSystem.Path.Combine(source, bundle.File!), path, true);
        }
        string target = _fileSystem.Path.Combine(destination, "manifest.json");
        EnsureNoLinks(target);
        _fileSystem.File.Copy(_fileSystem.Path.Combine(source, "manifest.json"), target, true);
    }

    private void Touch(string entry)
    {
        string path = _fileSystem.Path.Combine(entry, LAST_USED);
        EnsureNoLinks(path);
        _fileSystem.File.WriteAllBytes(path, Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
    }

    private bool HasLink(string path) => (_fileSystem.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private void EnsureNoLinks(string path)
    {
        for (string? current = _fileSystem.Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = _fileSystem.Path.GetDirectoryName(current))
        {
            try
            {
                if (HasLink(current))
                    throw new IOException("Shader cache paths cannot follow filesystem links: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private void EnsureRegularTree(string directory)
    {
        EnsureNoLinks(directory);
        foreach (string path in _fileSystem.Directory.EnumerateFileSystemEntries(directory))
        {
            if (HasLink(path))
                throw new IOException("A shader cache entry contains a filesystem link.");
            if (_fileSystem.Directory.Exists(path))
                EnsureRegularTree(path);
        }
    }
}
