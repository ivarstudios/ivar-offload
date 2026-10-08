using System.IO;
using IvarOffload.Core.IO;
using IvarOffload.Core.Sorting;

namespace IvarOffload.App;

/// <summary>Whether a job's source is a memory card or camera drive, for a job resumed or ended without a new preview.</summary>
internal static class CardDrive
{
    /// <summary>
    /// "F: SONY_A" when <paramref name="source"/> looks like a memory card or camera drive (as the preview flags it),
    /// null when it does not or can't be reached. Never throws.
    /// </summary>
    public static string? Of(string source)
    {
        if (source.Length == 0 || SourceGuards.MemoryCardNote(source) is null) return null;
        try
        {
            return SourceGuards.DriveName(VolumeInfo.Of(source));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
