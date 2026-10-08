namespace IvarOffload.Core.Jobs;

/// <summary>
/// Why a planned file was skipped (a final state: the file stays where it is). The texts are written to the job log
/// and shown to the user after "3 files: " (no capital, no final period). Logs written by older versions hold older
/// texts: read a skip reason from a log with <see cref="Current"/> and the helpers below, never by comparing it with a
/// constant directly. <see cref="SourceGone"/> never changes (Journal.cs compares it directly).
/// </summary>
public static class SkipReasons
{
    public const string IdenticalInTarget = "an identical copy is already in the target folder (the job kept the source file)";
    public const string DifferentInTarget = "a different file with the same name is in the target folder (the job kept the source file)";
    public const string SameNameAndSizeInTarget = "a file with the same name and size is in the target folder (the job kept the source file)";
    public const string FolderInTarget = "a folder with the same name is in the target folder (the job kept the source file)";
    public const string AppearedDuringMove = "a file appeared in the target folder during the move (the job kept the source file)";
    public const string AppearedDuringCopy = "a file appeared in the target folder during the copy (the job kept the source file)";
    public const string ChangedAfterPreview = "the source file changed after the preview (the job does not move a changed file)";
    public const string ChangedDuringMove = "the source file changed during the move (the job does not move a changed file)";
    /// <summary>
    /// The file disappeared from the source (its folder is still there) before it was moved: something else removed it.
    /// It is in neither folder, so it is reported on its own, never as "still in the source" (see <see cref="JobItem.IsMissing"/>).
    /// Read back by Journal.cs as it is: never change this text.
    /// </summary>
    public const string SourceGone = "the source file no longer exists";
    public const string Closed = "not moved because you ended the job early";

    // Undo jobs ("target" is the folder the files originally came from).
    public const string AlreadyBackIdentical = "an identical copy is already back in its original place (the undo kept this copy too)";
    public const string AlreadyBackDifferent = "a different file is now in its original place (the undo kept both files)";
    public const string AlreadyBackSameSize = "a file with the same name and size is now in its original place (the undo kept both files)";
    public const string ChangedSinceSorted = "changed after the sort (the undo does not move a changed file)";
    /// <summary>No longer in the sorted folder, and the same file is in its original place (an earlier undo moved it back).</summary>
    public const string AlreadyBack = "already back in its original place";
    public const string NotInSortedFolder = "no longer in the target folder (someone moved, renamed or deleted files there after the sort)";

    private const string KeptWithPrefix = "kept with ";

    /// <summary>A companion held back because another file of its group (usually its clip) was not moved.</summary>
    public static string KeptWith(string name) => $"{KeptWithPrefix}{name}, which did not move";

    public static bool IsKeptWithGroup(string? reason) => reason?.StartsWith(KeptWithPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// The one skip that leaves nothing behind: the same content is already in the target, so the file in the source
    /// is a spare copy (or, for an undo, the file is already back). Every other skip means a planned file is still
    /// (only) in the source.
    /// </summary>
    public static bool IsIdenticalInTarget(string? reason) => Current(reason) is IdenticalInTarget or AlreadyBackIdentical or AlreadyBack;

    /// <summary>A skip reason from a job log in today's words: the same reason as an older version wrote it becomes the constant above.</summary>
    public static string? Current(string? reason) => reason switch
    {
        "an identical copy already exists in the target (source kept)" => IdenticalInTarget,
        "a different file with the same name exists in the target (source kept)" => DifferentInTarget,
        "a file with the same name and size exists in the target (source kept)" => SameNameAndSizeInTarget,
        "a folder with the same name exists in the target (source kept)" => FolderInTarget,
        "a file appeared in the target during the move (source kept)" => AppearedDuringMove,
        "a file appeared in the target during the copy (source kept)" => AppearedDuringCopy,
        "the source file changed after the preview (left in place)" => ChangedAfterPreview,
        "the source file changed during the move (left in place)" => ChangedDuringMove,
        "not moved - the job was closed before this file was reached" => Closed,
        "an identical copy is already back in its original place (this copy was kept)" => AlreadyBackIdentical,
        "a different file is now in its original place - both kept" => AlreadyBackDifferent,
        "a file with the same name and size is now in its original place - both kept" => AlreadyBackSameSize,
        "changed since it was sorted - left where it is" => ChangedSinceSorted,
        "no longer in the sorted folder (moved, renamed or deleted since the sort)" => NotInSortedFolder,
        _ => reason,
    };
}

/// <summary>Plain-language errors recorded for files that could not be moved. A failed file stays open for Resume.</summary>
public static class FailReasons
{
    public const string SourceReadTwiceDiffers =
        "The source drive gave different data when the job read this file two times, so the job kept the original. Check the drive, the cable and the port.";
    public const string CopyChangedByOtherProgram = "another program changed the copy in the target folder, so the job kept both files";
    public const string OriginalNotRemovable =
        "A checked copy is in the target folder, but the job could not delete the original from the source folder (read-only, or no permission). "
        + "The job kept the original.";
    public const string NotMovable = "The job could not move the file out of the source folder (read-only, or no permission). The file stays where it is.";
    public const string TooLargeForTarget =
        "This file is 4 GB or larger, and the target drive (FAT32) cannot store a file of this size. The file stays in the source folder. "
        + "Use an NTFS or exFAT drive for the target folder.";
    public const string StreamsNotSupported =
        "This file has extra hidden metadata (alternate data streams, for example Mac Finder tags or labels) that the target drive cannot store. "
        + "The file stays in the source folder.";
    public const string OriginalChangedAfterCopy =
        "The original changed after the job copied it, so the job did not delete it. The job kept both versions. Check them.";
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
    public static string WaitingFor(string name) => $"{WaitingPrefix}{name}, which could not move yet";

    public static bool IsWaiting(string? reason) => reason?.StartsWith(WaitingPrefix, StringComparison.Ordinal) == true;

    public static string FolderMissing(string folder) => $"the folder {folder} is missing. Did someone rename it or disconnect its drive?";

    private const string DeviceErrorPrefix = "the drive stopped responding while this file was being moved (";

    /// <summary>A "device not ready / I/O device error" on this file. The file was left in the source.</summary>
    public static string DeviceError(string detail) => $"{DeviceErrorPrefix}{detail}). The file is still in the source folder";

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
