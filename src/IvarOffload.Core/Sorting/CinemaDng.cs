using System.Buffers.Binary;
using IvarOffload.Core.IO;
using Microsoft.Win32.SafeHandles;

namespace IvarOffload.Core.Sorting;

/// <summary>
/// Tells a frame of a CinemaDNG movie from a still DNG photo. Both are DNG files; only movie frames carry the
/// CinemaDNG tags (time code, frame rate, T-stop, reel name, camera label) in their first image directory.
/// </summary>
public static class CinemaDng
{
    /// <summary>TimeCodes, FrameRate, TStop, ReelName and CameraLabel: the CinemaDNG tags, all written to IFD0.</summary>
    private static readonly ushort[] MovieTags = [0xC763, 0xC764, 0xC772, 0xC789, 0xC7A1];

    /// <summary>More entries than any real image directory has: the file is not a TIFF/DNG after all.</summary>
    private const int MaxEntries = 4096;

    /// <summary>
    /// True when the file is a CinemaDNG movie frame. Reads a few hundred bytes without changing anything (not even
    /// the last-access time). False for still photos, other files and files that cannot be read. Never throws.
    /// </summary>
    public static bool IsMovieFrame(string path)
    {
        try
        {
            using SafeFileHandle h = SafeFile.OpenRead(path, unbuffered: false);
            return HasMovieTags((buffer, offset) => RandomAccess.Read(h, buffer, offset));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Reads the TIFF header and IFD0 through <paramref name="read"/> (buffer, file offset) and looks for a CinemaDNG tag.</summary>
    internal static bool HasMovieTags(Func<byte[], long, int> read)
    {
        byte[] header = new byte[8];
        if (read(header, 0) < header.Length) return false;
        bool little = header[0] == 'I' && header[1] == 'I';
        if (!little && !(header[0] == 'M' && header[1] == 'M')) return false;
        if (U16(header, 0, 2, little) != 42) return false;
        long ifd = U32(header, 0, 4, little);
        if (ifd < 8) return false;

        byte[] count = new byte[2];
        if (read(count, ifd) < count.Length) return false;
        int entries = U16(count, 0, 0, little);
        if (entries is 0 or > MaxEntries) return false;
        byte[] directory = new byte[entries * 12];
        int got = read(directory, ifd + 2);
        for (int i = 0; i + 12 <= got; i += 12)
            if (Array.IndexOf(MovieTags, U16(directory, i, 0, little)) >= 0)
                return true;
        return false;
    }

    private static ushort U16(byte[] b, int start, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(start + at)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(start + at));

    private static uint U32(byte[] b, int start, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(start + at)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(start + at));
}
