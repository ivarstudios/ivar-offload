using System.IO;
using IvarOffload.Core.IO;

namespace IvarOffload.App;

/// <summary>Where a job log that can't be found went: its drive is gone, or its folder was renamed or moved.</summary>
internal static class LogDrive
{
    /// <summary>
    /// Why this job log is not where it was: its drive is not connected, its folder was renamed or moved (the drive
    /// answers with the label the job recorded), or one of the two. Never throws.
    /// </summary>
    public static MissingLog Why(string journalPath, string label) => JobTexts.WhyLogMissing(journalPath, label, FolderExists, LabelOf);

    private static bool FolderExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string? LabelOf(string root)
    {
        try
        {
            return VolumeInfo.Of(root).Label;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
