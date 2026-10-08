using System.Buffers.Binary;
using IvarOffload.Core.IO;
using Microsoft.Win32.SafeHandles;

namespace IvarOffload.Core.Sorting;

/// <summary>
/// Tells the motion clip of an Apple Live Photo from an ordinary video with the same name as a photo (Canon bodies and
/// iPhones both count IMG_0001, IMG_0002, ...). Only a Live Photo clip carries Apple's content identifier, the key that
/// pairs it with its photo, in its QuickTime metadata (moov\meta\keys).
/// </summary>
public static class LivePhotoClip
{
    private static readonly byte[] ContentIdentifierKey = "com.apple.quicktime.content.identifier"u8.ToArray();

    /// <summary>A Live Photo clip's metadata box is a few tens of KB; a much larger one belongs to a long video.</summary>
    private const int MaxMovieBox = 4 * 1024 * 1024;

    /// <summary>A QuickTime file has a handful of top-level boxes (ftyp, wide, mdat, moov, free).</summary>
    private const int MaxTopLevelBoxes = 64;

    /// <summary>
    /// True when the file is an Apple Live Photo clip. Reads only the box headers and the metadata box, without changing
    /// anything (not even the last-access time). False for other videos, other files and files that cannot be read.
    /// Never throws.
    /// </summary>
    public static bool IsLivePhotoClip(string path)
    {
        try
        {
            using SafeFileHandle h = SafeFile.OpenRead(path, unbuffered: false);
            return HasContentIdentifier((buffer, offset) => ReadFully(h, buffer, offset), RandomAccess.GetLength(h));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Walks the top-level boxes through <paramref name="read"/> (buffer, file offset) and looks in "moov" for the key.</summary>
    internal static bool HasContentIdentifier(Func<byte[], long, int> read, long length)
    {
        byte[] header = new byte[16];
        long offset = 0;
        for (int i = 0; i < MaxTopLevelBoxes && offset + 8 <= length; i++)
        {
            int got = read(header, offset);
            if (got < 8) return false;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            int headerSize = 8;
            if (size == 1)
            {
                if (got < 16) return false;
                size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)), long.MaxValue);
                headerSize = 16;
            }
            else if (size == 0)
                size = length - offset; // the last box runs to the end of the file
            if (size < headerSize) return false; // not a QuickTime / MP4 file

            if (header.AsSpan(4, 4).SequenceEqual("moov"u8))
            {
                long body = Math.Min(size, length - offset) - headerSize;
                if (body <= 0 || body > MaxMovieBox) return false;
                byte[] movie = new byte[body];
                int n = read(movie, offset + headerSize);
                return movie.AsSpan(0, n).IndexOf(ContentIdentifierKey) >= 0;
            }
            if (size >= length - offset) return false; // the last box, and it was not "moov"
            offset += size;
        }
        return false;
    }

    private static int ReadFully(SafeFileHandle h, byte[] buffer, long offset)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(h, buffer.AsSpan(total), offset + total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
