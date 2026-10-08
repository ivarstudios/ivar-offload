using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using static IvarOffload.Core.IO.Native;

namespace IvarOffload.Core.IO;

/// <summary>Metadata of a file as read through an open handle. Times are UTC FILETIME ticks.</summary>
public sealed record FileSnapshot(
    long Size,
    long CreationTime,
    long LastWriteTime,
    long LastAccessTime,
    FileAttributes Attributes,
    string? FileId,
    uint LinkCount)
{
    /// <summary>Same size, creation and modification time, and - when requested and known - the same file record.</summary>
    public bool SameFileAs(FileSnapshot other, bool compareFileId) =>
        Size == other.Size
        && LastWriteTime == other.LastWriteTime
        && CreationTime == other.CreationTime
        && (!compareFileId || FileId is null || other.FileId is null || FileId == other.FileId);

    public bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);
}

/// <summary>A Win32 error with the native error code preserved, so callers can decide to retry or stop.</summary>
public sealed class Win32IOException(int nativeError, string action, string path)
    : IOException($"{action} failed for \"{path}\": {new Win32Exception(nativeError).Message} (error {nativeError})")
{
    public int NativeError { get; } = nativeError;
    /// <summary>The file or folder the failed operation was about.</summary>
    public string Path { get; } = path;
    public bool IsSharingViolation => NativeError is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION;
    public bool IsDiskFull => NativeError is ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL;
    public bool IsNotFound => NativeError is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND;
    /// <summary>No permission, or a write-protected drive.</summary>
    public bool IsAccessDenied => NativeError is ERROR_ACCESS_DENIED or ERROR_WRITE_PROTECT;
    public bool IsDeviceGone => IOErrors.IsDeviceGoneCode(NativeError);
}

/// <summary>Classifies I/O exceptions by their Win32 error, whether they come from this tool's own calls or from .NET.</summary>
public static class IOErrors
{
    /// <summary>
    /// Errors that mean the card or drive itself went away or stopped answering (unplugged, reader reset, network
    /// share gone), as opposed to a problem with one file.
    /// </summary>
    public static bool IsDeviceGoneCode(int error) => error is ERROR_INVALID_DRIVE or ERROR_NOT_READY or ERROR_BAD_NETPATH
        or ERROR_DEV_NOT_EXIST or ERROR_NETNAME_DELETED or ERROR_BAD_NET_NAME or ERROR_SEM_TIMEOUT or ERROR_NO_SUCH_DEVICE
        or ERROR_FILE_INVALID or ERROR_MEDIA_CHANGED or ERROR_IO_DEVICE or ERROR_DEVICE_NOT_CONNECTED or ERROR_DEVICE_REMOVED;

    /// <summary>The Win32 error behind an exception: the tool's own code, or the one .NET put in the HRESULT (0x8007xxxx).</summary>
    public static int? NativeError(Exception e) => e switch
    {
        Win32IOException w => w.NativeError,
        IOException or UnauthorizedAccessException when ((uint)e.HResult & 0xFFFF0000) == 0x80070000 => e.HResult & 0xFFFF,
        _ => null,
    };

    public static bool IsDeviceGone(Exception e) => NativeError(e) is int n && IsDeviceGoneCode(n);

    public static bool IsDiskFull(Exception e) => NativeError(e) is ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL;

    public static bool IsFileTooLarge(Exception e) => NativeError(e) is ERROR_FILE_TOO_LARGE;

    public static bool IsAccessDenied(Exception e) => e is UnauthorizedAccessException || NativeError(e) is ERROR_ACCESS_DENIED or ERROR_WRITE_PROTECT;
}

/// <summary>A named (alternate) data stream of a file. <see cref="Name"/> is in Win32 form, e.g. ":AFP_AfpInfo:$DATA".</summary>
public sealed record NamedStream(string Name, long Size)
{
    /// <summary>The "downloaded from the internet" mark. Harmless to lose, so it never blocks a move.</summary>
    public bool IsZoneIdentifier => Name.StartsWith(":Zone.Identifier:", StringComparison.OrdinalIgnoreCase);
}

public enum DeleteOutcome { Deleted, AlreadyGone, ChangedNotDeleted }

/// <summary>One new file of a multi-destination copy (see <see cref="SafeFile.CopyToNewFiles"/>).</summary>
public sealed class CopyDestination(string path)
{
    public string Path { get; } = path;
    /// <summary>Why this destination dropped out; null when its file was written completely and flushed.</summary>
    public Exception? Error { get; internal set; }
}

/// <summary>Checksums of the bytes read from the source, as lowercase hex (xxHash64 big-endian, as ASC MHL writes it).</summary>
public sealed record MultiCopyHashes(string Sha256, string Xxh64);

/// <summary>
/// File operations with the guarantees this tool depends on: reading never updates last-access times,
/// hashing reads from disk rather than the cache, files are never overwritten, and a source file is only
/// deleted through a handle whose identity has just been checked.
/// </summary>
public static unsafe class SafeFile
{
    public const int ChunkSize = 8 * 1024 * 1024;
    private const int Alignment = 64 * 1024;

    /// <summary>The attributes a move or copy keeps (<see cref="ApplyTimesAndAttributes"/> sets them).</summary>
    public const FileAttributes CopyableAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed;

    /// <summary>Win32 extended-length form of a path, so paths over 260 characters work in native calls.</summary>
    public static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        string full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    /// <summary>Metadata of a file or directory without opening its data (does not touch last-access time). Null if it does not exist.</summary>
    public static FileSnapshot? TrySnapshot(string path)
    {
        using SafeFileHandle h = CreateFile(ToExtendedPath(path), FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, 0, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, 0);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            if (err is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) return null;
            throw new Win32IOException(err, "Reading file information", path);
        }
        return Snapshot(h, path);
    }

    public static bool Exists(string path) => TrySnapshot(path) is not null;

    /// <summary>True when the folder exists and its drive answers. A drive that is gone or not ready counts as "does not exist".</summary>
    public static bool DirectoryExists(string path)
    {
        try
        {
            return TrySnapshot(path) is { IsDirectory: true };
        }
        catch (Win32IOException e) when (e.IsDeviceGone || e.NativeError is ERROR_INVALID_PARAMETER or ERROR_INVALID_NAME)
        {
            return false;
        }
    }

    /// <summary>
    /// Where a folder really is, so that two paths to the same folder compare equal: junctions, symbolic links, subst
    /// and mapped network drive letters and short (8.3) names are resolved. A folder that does not exist yet is
    /// resolved through its nearest existing parent. Never throws: returns the full path as given when it cannot tell.
    /// </summary>
    public static string RealPath(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
        try
        {
            string existing = full;
            var missing = new List<string>();
            while (!Directory.Exists(existing))
            {
                if (Path.GetDirectoryName(existing) is not { } parent) return full;
                missing.Insert(0, Path.GetFileName(existing));
                existing = parent;
            }
            using SafeFileHandle h = CreateFile(ToExtendedPath(existing), FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
            if (h.IsInvalid) return full;
            string? real = FinalPath(h);
            if (real is null) return full;
            real = real.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + real[8..]
                : real.StartsWith(@"\\?\", StringComparison.Ordinal) ? real[4..]
                : real;
            return Path.TrimEndingDirectorySeparator(Path.Join([real, .. missing]));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return full;
        }
    }

    private static string? FinalPath(SafeFileHandle h)
    {
        char* small = stackalloc char[512];
        uint n = GetFinalPathNameByHandle(h, small, 512, VOLUME_NAME_DOS);
        if (n == 0) return null;
        if (n < 512) return new string(small, 0, (int)n);
        char[] large = new char[n + 1]; // n is the size needed, including the terminating zero
        fixed (char* p = large)
        {
            uint m = GetFinalPathNameByHandle(h, p, (uint)large.Length, VOLUME_NAME_DOS);
            return m == 0 || m >= large.Length ? null : new string(p, 0, (int)m);
        }
    }

    internal static FileSnapshot Snapshot(SafeFileHandle h, string path)
    {
        FILE_BASIC_INFO basic;
        FILE_STANDARD_INFO standard;
        FILE_ID_INFO id;
        if (!GetFileInformationByHandleEx(h, FileBasicInfo, &basic, (uint)sizeof(FILE_BASIC_INFO)))
            throw new Win32IOException(Marshal.GetLastPInvokeError(), "Reading file times", path);
        if (!GetFileInformationByHandleEx(h, FileStandardInfo, &standard, (uint)sizeof(FILE_STANDARD_INFO)))
            throw new Win32IOException(Marshal.GetLastPInvokeError(), "Reading file size", path);
        string? fileId = GetFileInformationByHandleEx(h, FileIdInfo, &id, (uint)sizeof(FILE_ID_INFO))
            ? $"{id.VolumeSerialNumber:x16}-{id.FileIdHigh:x16}{id.FileIdLow:x16}"
            : null;
        return new FileSnapshot(standard.EndOfFile, basic.CreationTime, basic.LastWriteTime, basic.LastAccessTime,
            (FileAttributes)basic.FileAttributes, fileId, standard.NumberOfLinks);
    }

    /// <summary>
    /// Opens a file for reading its content. Other processes may read but not write or delete it while open.
    /// The handle is told not to update the last-access time, so reading leaves every timestamp as it was.
    /// </summary>
    public static SafeFileHandle OpenRead(string path, bool unbuffered) => OpenReadExtended(ToExtendedPath(path), path, unbuffered);

    private static SafeFileHandle OpenReadExtended(string p, string path, bool unbuffered)
    {
        uint flags = FILE_FLAG_SEQUENTIAL_SCAN | (unbuffered ? FILE_FLAG_NO_BUFFERING : 0);
        // FILE_WRITE_ATTRIBUTES is only requested so SetFileTime(-1) can suspend last-access updates; nothing is written.
        SafeFileHandle h = CreateFile(p, GENERIC_READ | FILE_WRITE_ATTRIBUTES, FILE_SHARE_READ, 0, OPEN_EXISTING, flags, 0);
        if (!h.IsInvalid)
        {
            long preserve = -1;
            SetFileTime(h, 0, ref preserve, 0);
            return h;
        }
        int err = Marshal.GetLastPInvokeError();
        h.Dispose();
        if (err is not (ERROR_ACCESS_DENIED or ERROR_WRITE_PROTECT)) throw new Win32IOException(err, "Opening", path);

        // No permission to suspend access-time updates (a read-only share or a write-protected drive): read anyway.
        h = CreateFile(p, GENERIC_READ, FILE_SHARE_READ, 0, OPEN_EXISTING, flags, 0);
        if (h.IsInvalid)
        {
            err = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw new Win32IOException(err, "Opening", path);
        }
        return h;
    }

    /// <summary>SHA-256 of a file's content, read from disk (bypassing the cache when the volume allows it).</summary>
    public static byte[] HashFile(string path, long expectedLength, Action<long>? onProgress, CancellationToken ct) =>
        HashExtended(ToExtendedPath(path), path, expectedLength, onProgress, ct);

    private static byte[] HashExtended(string p, string path, long expectedLength, Action<long>? onProgress, CancellationToken ct)
    {
        try
        {
            using SafeFileHandle h = OpenReadExtended(p, path, unbuffered: true);
            return HashHandle(h, expectedLength, onProgress, ct);
        }
        catch (Win32IOException e) when (e.NativeError == ERROR_INVALID_PARAMETER)
        {
            // Some file systems refuse unbuffered I/O; fall back to a normal read.
            using SafeFileHandle h = OpenReadExtended(p, path, unbuffered: false);
            return HashHandle(h, expectedLength, onProgress, ct);
        }
    }

    // ---- Alternate data streams ------------------------------------------------------------------------------

    /// <summary>The named data streams of a file (not its main content). Empty on file systems without streams (FAT, exFAT).</summary>
    public static List<NamedStream> NamedStreams(string path)
    {
        var result = new List<NamedStream>();
        WIN32_FIND_STREAM_DATA data;
        nint find = FindFirstStream(ToExtendedPath(path), FindStreamInfoStandard, &data, 0);
        if (find == INVALID_HANDLE_VALUE)
        {
            int err = Marshal.GetLastPInvokeError();
            if (err is ERROR_HANDLE_EOF or ERROR_INVALID_PARAMETER or ERROR_NOT_SUPPORTED or ERROR_INVALID_FUNCTION) return result;
            throw new Win32IOException(err, "Listing data streams", path);
        }
        try
        {
            while (true)
            {
                string name = new(data.StreamName);
                if (!name.Equals("::$DATA", StringComparison.OrdinalIgnoreCase)) result.Add(new NamedStream(name, data.StreamSize));
                if (FindNextStream(find, &data)) continue;
                int err = Marshal.GetLastPInvokeError();
                if (err != ERROR_HANDLE_EOF) throw new Win32IOException(err, "Listing data streams", path);
                return result;
            }
        }
        finally
        {
            FindClose(find);
        }
    }

    /// <summary>
    /// Copies one named stream of a file into the same stream of another file (created new, never overwritten) and
    /// flushes it to disk. Returns the SHA-256 of the bytes read from the source.
    /// </summary>
    public static byte[] CopyStream(string sourceFile, string destinationFile, NamedStream stream, CancellationToken ct)
    {
        string destination = ToExtendedPath(destinationFile) + stream.Name;
        using SafeFileHandle source = OpenReadExtended(ToExtendedPath(sourceFile) + stream.Name, sourceFile + stream.Name, unbuffered: false);
        using SafeFileHandle dest = CreateFile(destination, GENERIC_WRITE, 0, 0, CREATE_NEW, 0, 0);
        if (dest.IsInvalid) throw new Win32IOException(Marshal.GetLastPInvokeError(), "Creating data stream", destinationFile + stream.Name);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        long offset = 0;
        int n;
        while ((n = RandomAccess.Read(source, buffer, offset)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            RandomAccess.Write(dest, buffer.AsSpan(0, n), offset);
            sha.AppendData(buffer, 0, n);
            offset += n;
        }
        if (offset != stream.Size)
            throw new IOException($"Copied {offset:N0} bytes of data stream {stream.Name} but it should be {stream.Size:N0} bytes; it may have changed.");
        if (!FlushFileBuffers(dest))
            throw new Win32IOException(Marshal.GetLastPInvokeError(), "Flushing to disk", destinationFile + stream.Name);
        return sha.GetHashAndReset();
    }

    /// <summary>SHA-256 of one named stream of a file, read from disk where the volume allows it.</summary>
    public static byte[] HashStream(string file, NamedStream stream, CancellationToken ct) =>
        HashExtended(ToExtendedPath(file) + stream.Name, file + stream.Name, stream.Size, null, ct);

    internal static byte[] HashHandle(SafeFileHandle h, long expectedLength, Action<long>? onProgress, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var bufA = new AlignedBuffer(ChunkSize, Alignment);
        using var bufB = new AlignedBuffer(ChunkSize, Alignment);
        AlignedBuffer current = bufA, next = bufB;
        long offset = 0;
        Task<int>? pendingRead = null;
        try
        {
            int n = ReadChunk(h, current, offset);
            while (n > 0)
            {
                ct.ThrowIfCancellationRequested();
                long nextOffset = offset + n;
                if (n == ChunkSize)
                {
                    AlignedBuffer target = next;
                    pendingRead = Task.Run(() => ReadChunk(h, target, nextOffset));
                }
                sha.AppendData(current.Span[..n]);
                onProgress?.Invoke(n);
                n = pendingRead?.GetAwaiter().GetResult() ?? 0;
                pendingRead = null;
                offset = nextOffset;
                (current, next) = (next, current);
            }
        }
        finally
        {
            // Never release a buffer while a read may still be writing into it.
            try { pendingRead?.Wait(); } catch { /* the original exception is what matters */ }
        }
        if (offset != expectedLength)
            throw new IOException($"Read {offset:N0} bytes but the file should be {expectedLength:N0} bytes; it may have changed.");
        return sha.GetHashAndReset();
    }

    /// <summary>
    /// Copies an open source file into a new file (never overwriting), hashing the source bytes as they are read.
    /// The new file is flushed to disk before returning. Returns the SHA-256 of the source content.
    /// </summary>
    public static byte[] CopyToNewFile(SafeFileHandle source, string destinationPath, long length, Action<long>? onProgress, CancellationToken ct)
    {
        using SafeFileHandle dest = File.OpenHandle(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            FileOptions.None, preallocationSize: length);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var bufA = new AlignedBuffer(ChunkSize, Alignment);
        using var bufB = new AlignedBuffer(ChunkSize, Alignment);
        AlignedBuffer current = bufA, next = bufB;
        long offset = 0;
        Task<int>? pendingRead = null;
        Task? pendingWrite = null;
        try
        {
            int n = ReadChunk(source, current, offset);
            while (n > 0)
            {
                ct.ThrowIfCancellationRequested();
                long chunkOffset = offset, nextOffset = offset + n;
                int count = n;
                AlignedBuffer chunk = current;
                if (n == ChunkSize)
                {
                    AlignedBuffer target = next;
                    pendingRead = Task.Run(() => ReadChunk(source, target, nextOffset));
                }
                pendingWrite = Task.Run(() => RandomAccess.Write(dest, chunk.Span[..count], chunkOffset));
                sha.AppendData(current.Span[..n]);
                pendingWrite.GetAwaiter().GetResult();
                pendingWrite = null;
                onProgress?.Invoke(n);
                n = pendingRead?.GetAwaiter().GetResult() ?? 0;
                pendingRead = null;
                offset = nextOffset;
                (current, next) = (next, current);
            }
        }
        finally
        {
            try { pendingWrite?.Wait(); } catch { }
            try { pendingRead?.Wait(); } catch { }
        }
        if (offset != length)
            throw new IOException($"Copied {offset:N0} bytes but the source should be {length:N0} bytes; it may have changed.");
        if (!FlushFileBuffers(dest))
            throw new Win32IOException(Marshal.GetLastPInvokeError(), "Flushing to disk", destinationPath);
        return sha.GetHashAndReset();
    }

    /// <summary>
    /// Copies an open source file into a new file on each destination (never overwriting), reading the source once:
    /// every chunk is written to all destinations at the same time and hashed once (SHA-256 and xxHash64). A
    /// destination that fails (cannot be created, full, unplugged) drops out with its <see cref="CopyDestination.Error"/>
    /// set, and the others go on; its partial file is left for the caller to remove. Every completed file is flushed to
    /// disk. Throws only for a problem with the source (or cancellation), after every pending write has finished.
    /// Returns null when every destination dropped out.
    /// </summary>
    public static MultiCopyHashes? CopyToNewFiles(SafeFileHandle source, IReadOnlyList<CopyDestination> destinations, long length,
        Action<long>? onProgress, CancellationToken ct)
    {
        var open = new List<(CopyDestination Destination, SafeFileHandle Handle)>();
        foreach (CopyDestination d in destinations)
        {
            try
            {
                open.Add((d, File.OpenHandle(d.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.None, preallocationSize: length)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                d.Error = e;
            }
        }
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var xxh = new System.IO.Hashing.XxHash64();
            using var bufA = new AlignedBuffer(ChunkSize, Alignment);
            using var bufB = new AlignedBuffer(ChunkSize, Alignment);
            AlignedBuffer current = bufA, next = bufB;
            long offset = 0;
            Task<int>? pendingRead = null;
            try
            {
                if (open.Count == 0) return null; // no destination could even create its file: nothing is read
                int n = ReadChunk(source, current, offset);
                while (n > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    long chunkOffset = offset, nextOffset = offset + n;
                    int count = n;
                    AlignedBuffer chunk = current;
                    if (n == ChunkSize)
                    {
                        AlignedBuffer target = next;
                        pendingRead = Task.Run(() => ReadChunk(source, target, nextOffset));
                    }
                    Task[] writes = open.Select(o => Task.Run(() => RandomAccess.Write(o.Handle, chunk.Span[..count], chunkOffset))).ToArray();
                    sha.AppendData(current.Span[..n]);
                    xxh.Append(current.Span[..n]);
                    DropFailed(open, writes);
                    onProgress?.Invoke(n);
                    n = pendingRead?.GetAwaiter().GetResult() ?? 0;
                    pendingRead = null;
                    offset = nextOffset;
                    (current, next) = (next, current);
                    if (open.Count == 0) return null; // every destination failed: stop reading
                }
            }
            finally
            {
                // Never release a buffer while a read may still be writing into it.
                try { pendingRead?.Wait(); } catch { /* the original exception is what matters */ }
            }
            if (offset != length)
                throw new IOException($"Read {offset:N0} bytes but the file should be {length:N0} bytes; it may have changed.");
            Task[] flushes = open.Select(o => Task.Run(() =>
            {
                if (!FlushFileBuffers(o.Handle)) throw new Win32IOException(Marshal.GetLastPInvokeError(), "Flushing to disk", o.Destination.Path);
            })).ToArray();
            DropFailed(open, flushes);
            return open.Count == 0 ? null : new MultiCopyHashes(ToHex(sha.GetHashAndReset()), ToHex(xxh.GetCurrentHash()));
        }
        finally
        {
            foreach ((_, SafeFileHandle handle) in open) handle.Dispose();
        }
    }

    /// <summary>Waits for one write (or flush) per open destination; the ones that failed are closed and dropped.</summary>
    private static void DropFailed(List<(CopyDestination Destination, SafeFileHandle Handle)> open, Task[] tasks)
    {
        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException)
        {
            // Looked at one by one below.
        }
        for (int i = tasks.Length - 1; i >= 0; i--)
        {
            if (!tasks[i].IsFaulted) continue;
            Exception e = tasks[i].Exception!.InnerException ?? tasks[i].Exception!;
            open[i].Destination.Error = e is IOException or UnauthorizedAccessException ? e : new IOException(e.Message, e);
            open[i].Handle.Dispose();
            open.RemoveAt(i);
        }
    }

    private static int ReadChunk(SafeFileHandle h, AlignedBuffer buffer, long offset)
    {
        try
        {
            return RandomAccess.Read(h, buffer.Span, offset);
        }
        catch (IOException e) when ((e.HResult & 0xFFFF) == ERROR_INVALID_PARAMETER)
        {
            throw new Win32IOException(ERROR_INVALID_PARAMETER, "Reading", "(open file)");
        }
    }

    /// <summary>Gives a file the timestamps and basic attributes recorded from another file.</summary>
    public static void ApplyTimesAndAttributes(string path, FileSnapshot from)
    {
        using SafeFileHandle h = CreateFile(ToExtendedPath(path), FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES,
            FILE_SHARE_READ, 0, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, 0);
        if (h.IsInvalid) throw new Win32IOException(Marshal.GetLastPInvokeError(), "Opening to set timestamps", path);
        uint attributes = (uint)(from.Attributes & CopyableAttributes);
        var info = new FILE_BASIC_INFO
        {
            CreationTime = from.CreationTime,
            LastAccessTime = from.LastAccessTime,
            LastWriteTime = from.LastWriteTime,
            ChangeTime = 0, // 0 = leave unchanged
            FileAttributes = attributes == 0 ? (uint)FileAttributes.Normal : attributes,
        };
        if (!SetFileInformationByHandle(h, FileBasicInfo, &info, (uint)sizeof(FILE_BASIC_INFO)))
            throw new Win32IOException(Marshal.GetLastPInvokeError(), "Setting timestamps", path);
    }

    /// <summary>Sets creation/modification times of a directory (used to mirror source folder dates).</summary>
    public static void SetDirectoryTimes(string path, long creationTime, long lastWriteTime)
    {
        using SafeFileHandle h = CreateFile(ToExtendedPath(path), FILE_WRITE_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (h.IsInvalid) throw new Win32IOException(Marshal.GetLastPInvokeError(), "Opening folder", path);
        var info = new FILE_BASIC_INFO { CreationTime = creationTime, LastWriteTime = lastWriteTime };
        if (!SetFileInformationByHandle(h, FileBasicInfo, &info, (uint)sizeof(FILE_BASIC_INFO)))
            throw new Win32IOException(Marshal.GetLastPInvokeError(), "Setting folder times", path);
    }

    /// <summary>
    /// Renames/moves a file within a volume. Never replaces an existing file. Returns 0 or the Win32 error code.
    /// </summary>
    public static int TryRename(string from, string to) =>
        MoveFileEx(ToExtendedPath(from), ToExtendedPath(to), MOVEFILE_WRITE_THROUGH) ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>
    /// Deletes a file only if it is still exactly the file described by <paramref name="expected"/>.
    /// The check and the delete happen through the same handle, and nobody may be writing to it.
    /// Returns <see cref="DeleteOutcome.AlreadyGone"/> only when the file is missing from a folder that is still
    /// there; a missing folder (renamed, or its drive disconnected) throws, because the file may well still exist.
    /// </summary>
    public static DeleteOutcome DeleteIfUnchanged(string path, FileSnapshot expected, bool compareFileId) =>
        DeleteThroughHandle(path, now => now.SameFileAs(expected, compareFileId));

    /// <summary>Deletes a temporary file this tool created itself.</summary>
    public static DeleteOutcome DeleteOwnTempFile(string path) => DeleteThroughHandle(path, now => !now.IsDirectory);

    private static DeleteOutcome DeleteThroughHandle(string path, Func<FileSnapshot, bool> stillSafeToDelete)
    {
        using SafeFileHandle h = CreateFile(ToExtendedPath(path), DELETE | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_DELETE, 0, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, 0);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            if (err == ERROR_FILE_NOT_FOUND && DirectoryExists(Path.GetDirectoryName(Path.GetFullPath(path))!)) return DeleteOutcome.AlreadyGone;
            throw new Win32IOException(err == ERROR_FILE_NOT_FOUND ? ERROR_PATH_NOT_FOUND : err, "Opening for delete", path);
        }
        FileSnapshot now = Snapshot(h, path);
        if (!stillSafeToDelete(now)) return DeleteOutcome.ChangedNotDeleted;

        uint flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;
        if (SetFileInformationByHandle(h, FileDispositionInfoEx, &flags, sizeof(uint))) return DeleteOutcome.Deleted;
        int error = Marshal.GetLastPInvokeError();
        if (error is not (ERROR_INVALID_PARAMETER or ERROR_NOT_SUPPORTED or ERROR_INVALID_FUNCTION))
            throw new Win32IOException(error, "Deleting", path);

        // File systems without POSIX delete (FAT/exFAT): clear read-only, then use the classic disposition.
        if (now.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            uint attrs = (uint)(now.Attributes & CopyableAttributes & ~FileAttributes.ReadOnly);
            var basic = new FILE_BASIC_INFO { FileAttributes = attrs == 0 ? (uint)FileAttributes.Normal : attrs };
            if (!SetFileInformationByHandle(h, FileBasicInfo, &basic, (uint)sizeof(FILE_BASIC_INFO)))
                throw new Win32IOException(Marshal.GetLastPInvokeError(), "Clearing read-only before delete", path);
        }
        byte delete = 1;
        if (!SetFileInformationByHandle(h, FileDispositionInfo, &delete, 1))
            throw new Win32IOException(Marshal.GetLastPInvokeError(), "Deleting", path);
        return DeleteOutcome.Deleted;
    }

    public static string ToHex(byte[] hash) => Convert.ToHexStringLower(hash);

    /// <summary>Native memory aligned for unbuffered I/O.</summary>
    internal sealed class AlignedBuffer : IDisposable
    {
        private byte* _pointer;
        public int Length { get; }

        public AlignedBuffer(int length, int alignment)
        {
            Length = length;
            _pointer = (byte*)NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment);
        }

        public Span<byte> Span => new(_pointer, Length);

        public void Dispose()
        {
            if (_pointer == null) return;
            NativeMemory.AlignedFree(_pointer);
            _pointer = null;
        }
    }
}
