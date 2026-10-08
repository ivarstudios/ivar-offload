namespace IvarOffload.Core.Jobs;

/// <summary>
/// Why a planned file was skipped (a final state: the file stays where it is). The texts are written to the job log
/// and shown to the user, so they must stay stable: older logs are read with the same helpers.
/// </summary>
public static class SkipReasons
{
    public const string IdenticalInTarget = "an identical copy already exists in the target (source kept)";
    public const string DifferentInTarget = "a different file with the same name exists in the target (source kept)";
    public const string SameNameAndSizeInTarget = "a file with the same name and size exists in the target (source kept)";
    public const string FolderInTarget = "a folder with the same name exists in the target (source kept)";
    public const string AppearedDuringMove = "a file appeared in the target during the move (source kept)";
    public const string AppearedDuringCopy = "a file appeared in the target during the copy (source kept)";
    public const string ChangedAfterPreview = "the source file changed after the preview (left in place)";
    public const string ChangedDuringMove = "the source file changed during the move (left in place)";
    /// <summary>
    /// The file disappeared from the source (its folder is still there) before it was moved: something else removed it.
    /// It is in neither folder, so it is reported on its own, never as "still in the source" (see <see cref="JobItem.IsMissing"/>).
    /// </summary>
    public const string SourceGone = "the source file no longer exists";
    public const string Closed = "not moved - the job was closed before this file was reached";

    // Undo jobs ("target" is the folder the files originally came from).
    public const string AlreadyBackIdentical = "an identical copy is already back in its original place (this copy was kept)";
    public const string AlreadyBackDifferent = "a different file is now in its original place - both kept";
    public const string AlreadyBackSameSize = "a file with the same name and size is now in its original place - both kept";
    public const string ChangedSinceSorted = "changed since it was sorted - left where it is";
    /// <summary>No longer in the sorted folder, and the same file is in its original place (an earlier undo moved it back).</summary>
    public const string AlreadyBack = "already back in its original place";
    public const string NotInSortedFolder = "no longer in the sorted folder (moved, renamed or deleted since the sort)";

    private const string KeptWithPrefix = "kept with ";

    /// <summary>A companion held back because another file of its group (usually its clip) was not moved.</summary>
    public static string KeptWith(string name) => $"{KeptWithPrefix}{name}, which was not moved";

    public static bool IsKeptWithGroup(string? reason) => reason?.StartsWith(KeptWithPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// The one skip that leaves nothing behind: the same content is already in the target, so the file in the source
    /// is a spare copy (or, for an undo, the file is already back). Every other skip means a planned file is still
    /// (only) in the source.
    /// </summary>
    public static bool IsIdenticalInTarget(string? reason) => reason is IdenticalInTarget or AlreadyBackIdentical or AlreadyBack;
}

/// <summary>Plain-language errors recorded for files that could not be moved. A failed file stays open for Resume.</summary>
public static class FailReasons
{
    public const string SourceReadTwiceDiffers =
        "The source drive returned different data when read twice - original kept. Check the drive, cable or port.";
    public const string CopyChangedByOtherProgram = "the copy in the target was changed by another program; both kept";
    public const string OriginalNotRemovable =
        "A verified copy is in the target, but the original could not be removed from the source (read-only or no permission). The original was kept.";
    public const string NotMovable = "The file could not be moved out of the source (read-only or no permission). It was left in place.";
    public const string TooLargeForTarget =
        "This file is 4 GB or larger and the target drive (FAT32) cannot store files that big. It was left in the source - use an NTFS or exFAT drive for the target.";
    public const string StreamsNotSupported =
        "This file carries extra hidden metadata (alternate data streams, for example Mac Finder tags or labels) that the target drive cannot store. It was left in the source.";
    public const string OriginalChangedAfterCopy =
        "The original changed after it was copied, so it was not deleted. Both versions were kept - please check them.";
    public const string InNeitherPlace = "Interrupted move: the file is in neither the source nor the target.";
    public const string CopyAndOriginalGone = "Interrupted copy: the copy is missing from the target and the original is no longer in the source.";
    public const string PlacedCopyDamagedOriginalGone =
        "The original is no longer in the source and the copy in the target does not match its checksum. The copy was kept - please check it.";

    private const string OriginalGonePrefix = "The original is no longer in the source";
    private const string OriginalDisappearedPrefix = "The original disappeared from the source";

    /// <summary>
    /// A failure that means the original is no longer in the source (something else removed it): the file is not
    /// "still in the source" (see <see cref="JobItem.IsMissing"/>), whatever copy of it was kept in the target.
    /// </summary>
    public static bool IsOriginalGone(string? reason) =>
        reason is InNeitherPlace or CopyAndOriginalGone
        || reason?.StartsWith(OriginalGonePrefix, StringComparison.Ordinal) == true
        || reason?.StartsWith(OriginalDisappearedPrefix, StringComparison.Ordinal) == true;

    /// <summary>Ending a job while the source could not be reached: the copy in the target is verified, the original was not touched.</summary>
    public const string OriginalNotChecked =
        "A verified copy is in the target, but the original could not be checked when the job was ended (the source folder or its drive was not there), "
        + "so it was not deleted. It is probably still in the source - please check it.";

    /// <summary>The original vanished while its copy was being made: the unfinished, unchecked copy is all that is left.</summary>
    public static string OriginalGoneDuringCopy(string kept) =>
        $"{OriginalDisappearedPrefix} while it was being copied. The copy in the target could not be checked, so it was kept as {kept} - please check it.";

    /// <summary>Ending a job while the source could not be reached: the unfinished copy is kept (the original was never touched).</summary>
    public static string UnfinishedCopyKept(string kept) =>
        $"Not moved: the job was ended while this file was being copied, and the original could not be checked (the source folder or its drive was not there). "
        + $"The unfinished copy was kept in the target as {kept}; the original was not touched.";

    /// <summary>
    /// Ending a job while the source could not be reached: the verified copy could not take its name in the target
    /// (another file has it), so it was kept under another name. The original was not touched and is probably still in
    /// the source - never "missing": it could not be looked at.
    /// </summary>
    public static string VerifiedCopyKeptNotChecked(string kept) =>
        $"Not moved: the job was ended while the original could not be checked (the source folder or its drive was not there), and a file with its name "
        + $"is already in the target. Its verified copy was kept in the target as {kept}; the original was not touched.";

    /// <summary>
    /// Ending a job while the source could not be reached: the copy does not match the checksum, and was kept (the
    /// original could not be looked at). The original was not touched and is probably still in the source.
    /// </summary>
    public static string DamagedCopyKeptNotChecked(string kept) =>
        $"Not moved: the job was ended while the original could not be checked (the source folder or its drive was not there), and its copy does not "
        + $"match the checksum. The copy was kept in the target as {kept}; the original was not touched.";

    /// <summary>Undo: the file changed since it was sorted and then disappeared from the sorted folder while it was being copied back.</summary>
    public static string ChangedCopyKept(string kept) =>
        $"{OriginalDisappearedPrefix} while it was being copied, and it had changed since it was sorted. Its copy was kept as {kept} - please check it.";

    /// <summary>The original is gone and its verified copy could not take its name in the target (another file has it).</summary>
    public static string VerifiedCopyKept(string kept) =>
        $"{OriginalGonePrefix}, and a file with its name is already in the target. Its verified copy was kept as {kept} - please check both.";

    /// <summary>The original is gone and its copy does not match the checksum: the damaged copy is all that is left.</summary>
    public static string DamagedCopyKept(string kept) =>
        $"{OriginalGonePrefix} and its copy does not match the checksum. The copy was kept as {kept} - please check it.";

    /// <summary>
    /// Whether <paramref name="reason"/> is a message made by <paramref name="format"/> (one of the messages above that
    /// name a kept copy, such as <see cref="VerifiedCopyKept"/>), whatever name the copy was kept under.
    /// </summary>
    public static bool IsMadeBy(string? reason, Func<string, string> format)
    {
        if (reason is null) return false;
        const char Mark = '\u0001';
        string sample = format(Mark.ToString());
        int at = sample.IndexOf(Mark);
        string before = sample[..at], after = sample[(at + 1)..];
        return reason.Length > before.Length + after.Length
               && reason.StartsWith(before, StringComparison.Ordinal) && reason.EndsWith(after, StringComparison.Ordinal);
    }

    private const string WaitingPrefix = "waiting for ";

    /// <summary>A file held back because an earlier file of its group could not be moved yet; Resume tries that one first.</summary>
    public static string WaitingFor(string name) => $"{WaitingPrefix}{name}, which could not be moved yet";

    public static bool IsWaiting(string? reason) => reason?.StartsWith(WaitingPrefix, StringComparison.Ordinal) == true;

    public static string FolderMissing(string folder) => $"the folder {folder} is missing - was it renamed or disconnected?";

    private const string DeviceErrorPrefix = "the drive stopped responding while this file was being moved (";

    /// <summary>A "device not ready / I/O device error" on this file. The file was left in the source.</summary>
    public static string DeviceError(string detail) => $"{DeviceErrorPrefix}{detail}) - it was left in the source";

    public static bool IsDeviceError(string? reason) => reason?.StartsWith(DeviceErrorPrefix, StringComparison.Ordinal) == true;
}

/// <summary>Why a job stopped by itself before it was finished. The job can be resumed once the cause is fixed.</summary>
public enum HaltReason
{
    None,
    /// <summary>The source folder or its drive is gone (unplugged, renamed, or another drive now has its letter).</summary>
    SourceNotConnected,
    /// <summary>The target folder or its drive is gone.</summary>
    TargetNotConnected,
    /// <summary>The job log is no longer in the target folder it was created in (the target was renamed or moved).</summary>
    LogMoved,
    /// <summary>A drive returned a "device not ready / removed" error.</summary>
    DriveStoppedResponding,
    TargetFull,
    /// <summary>Originals read differently the second time (copy path).</summary>
    SourceInconsistent,
    /// <summary>Originals cannot be removed (read-only drive or no permission).</summary>
    OriginalsNotRemovable,
    /// <summary>Copies in the target keep failing their checksum.</summary>
    TargetDamagingFiles,
}
