using IvarOffload.Core.IO;

namespace IvarOffload.Core.Jobs;

/// <summary>
/// Recognises the drives a job was made with by their volume serial number (recorded in the job log), also when
/// Windows gives them another drive letter (another PC, or another plug order).
/// </summary>
internal static class Drives
{
    /// <summary>Serial number of the volume that holds a path (or its nearest existing parent); 0 when unknown.</summary>
    public static uint SerialOf(string path)
    {
        try
        {
            return VolumeInfo.Of(path).SerialNumber;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The path's drive is connected and is the volume with this serial. An unknown serial (0, on either side) counts
    /// as a match, so older job logs and file systems without serial numbers keep working.
    /// </summary>
    public static bool IsOn(string path, uint serial)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || !SafeFile.DirectoryExists(root)) return false;
        if (serial == 0) return true;
        uint now = SerialOf(path);
        return now == 0 || now == serial;
    }

    /// <summary>
    /// The same path on every other drive letter whose volume has this serial number: the part below the drive root is
    /// kept, the letter changes. <paramref name="preferred"/>'s drive is tried first. Only local drives are searched.
    /// A job's path that was on a subst drive letter is followed by where that letter led (<see cref="OnDrive"/>).
    /// </summary>
    public static IEnumerable<string> Elsewhere(string path, uint serial, string? preferred = null) =>
        serial == 0 ? Enumerable.Empty<string>() : OnOtherLetters(path, preferred).Where(p => SerialOf(Path.GetPathRoot(p)!) == serial);

    /// <summary>
    /// The path whose drive letter stands for its volume: <paramref name="real"/> (where a subst drive letter led when
    /// the job was made; see <see cref="JobHeader.SourceReal"/>) when there is one, else the path itself. Only such a
    /// path may be looked for under another drive letter: below a subst letter lies another folder than below the root.
    /// </summary>
    public static string OnDrive(string path, string? real) => real is { Length: > 0 } ? real : path;

    /// <summary>
    /// The path still leads to the folder the job recorded for it: a path named through a subst drive letter
    /// (<paramref name="real"/> set) only while the letter still stands for the same folder - a subst letter can be
    /// given another folder at any time (or disappears at a restart), and then the path is another folder. Always true
    /// for a path that was not named through one.
    /// </summary>
    public static bool LeadsTo(string path, string? real)
    {
        if (real is not { Length: > 0 }) return true;
        try
        {
            return JobPaths.SamePath(VolumeInfo.Unsubst(Path.GetFullPath(path)), real);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The same path on every other local drive letter (<paramref name="preferred"/>'s drive first). Subst drive letters
    /// are never among them (they are folders, not drives), and a path on one has no other letters: its drive is elsewhere.
    /// </summary>
    public static IEnumerable<string> OnOtherLetters(string path, string? preferred = null)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || VolumeInfo.SubstTarget(root) is not null) yield break;
        string below = Path.GetRelativePath(root, path);
        var roots = new List<string>();
        if (preferred is not null && Path.GetPathRoot(Path.GetFullPath(preferred)) is { Length: > 0 } p) roots.Add(p);
        try
        {
            foreach (DriveInfo d in DriveInfo.GetDrives())
                if (d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram) roots.Add(d.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        foreach (string candidate in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(candidate.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            if (candidate.StartsWith(@"\\", StringComparison.Ordinal) || VolumeInfo.SubstTarget(candidate) is not null) continue;
            yield return below == "." ? candidate : Path.Join(candidate, below);
        }
    }

    /// <summary>
    /// Where a folder on a subst drive letter really is ("C:\Shoots\Card1" for "W:\Card1"), for the job's log; null when
    /// the folder is not on one (see <see cref="JobHeader.SourceReal"/>).
    /// </summary>
    public static string? RealIfSubst(string folder)
    {
        try
        {
            string full = Path.GetFullPath(folder);
            string real = VolumeInfo.Unsubst(full);
            return string.Equals(real, full, StringComparison.OrdinalIgnoreCase) ? null : Path.TrimEndingDirectorySeparator(real);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>"F:" for a path on F:, "\\server\share" for a network path.</summary>
    public static string Letter(string path) => (Path.GetPathRoot(path) ?? path).TrimEnd('\\');
}
