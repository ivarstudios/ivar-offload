using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Core.Jobs;

/// <summary>
/// One line of the job journal (JSON Lines). The journal is append-only and flushed to disk after every record,
/// so after a crash it tells exactly how far each file got.
/// </summary>
/// <remarks>
/// Record types: job (header), dir (original source folder times), item (a planned file), held (a file the preview
/// kept in the source, e.g. because a DIFFERENT file with its name is in the target; never part of the run), ready (plan complete),
/// session, mkdir, pre (same-drive move prepared), copy (copy started), copied (copy verified), placed (copy in its final place),
/// setaside (a copy was kept under another name: damaged, unverified or with its name taken), done, skip, fail, end,
/// rmdir, verify, undo (written into a sort job's journal when an undo job is created for it, and again when that undo
/// job ends, with the number of files it did not move back in "skipped").
/// Readers ignore record types they do not know, so newer journals stay readable.
/// Version 2 adds to the header: kind, undoes/undoesId, and the volume serial and label of source and target;
/// and to items: grp (the group of files that must stay together) and, for undo jobs, the expected sha256.
/// Later additions (readers of version 2 ignore them): pre records attr, the file's attributes before the rename (a
/// rename sets Archive, so they are put back); the header's srcReal/tgtReal (see <see cref="JobHeader.SourceReal"/>); and
/// the record rescan, what a fresh scan of the source found after the job ended (see <see cref="RescanRecord"/>).
/// Backup jobs (kind "backup", one log per destination, named *.backup.jsonl so no sort tool ever picks one up) add to
/// the header: targets/tgtSerials/tgtLabels (every destination of the backup, this one included) and reread (the second read of
/// the card); to copied/done: xxh64; and the record mhl (the ASC MHL generation written into the destination).
/// A top-up (a backup that adds a card's new files to its earlier backup, in the same folder) adds to the header: addsTo
/// (the earlier backup's job id); to items: sha256/xxh64 (what the earlier backup verified) and why "kept" (a file of the
/// earlier backup that is no longer on the card) or "chain" (an ASC MHL chain file whose copy carries this folder's own
/// generations); the records replace (the file in the folder is to be replaced by the card's, once that copy is
/// verified) and aside (it is being moved into _IVAROffload\replaced, with why; recorded before the move); and mhl with what
/// "restarted" (the folder's earlier ASC MHL history was moved aside and a new one started).
/// </remarks>
public sealed class JournalRecord
{
    [JsonPropertyName("t")] public string Type { get; set; } = "";
    [JsonPropertyName("i")] public int? Index { get; set; }
    [JsonPropertyName("v")] public int? Version { get; set; }
    [JsonPropertyName("id")] public string? JobId { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("undoes")] public string? Undoes { get; set; }
    [JsonPropertyName("undoesId")] public string? UndoesId { get; set; }
    [JsonPropertyName("addsTo")] public string? AddsTo { get; set; }
    [JsonPropertyName("tool")] public string? Tool { get; set; }
    [JsonPropertyName("machine")] public string? Machine { get; set; }
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("srcSerial")] public uint? SourceSerial { get; set; }
    [JsonPropertyName("srcLabel")] public string? SourceLabel { get; set; }
    [JsonPropertyName("tgtSerial")] public uint? TargetSerial { get; set; }
    [JsonPropertyName("tgtLabel")] public string? TargetLabel { get; set; }
    [JsonPropertyName("srcReal")] public string? SourceReal { get; set; }
    [JsonPropertyName("tgtReal")] public string? TargetReal { get; set; }
    [JsonPropertyName("targets")] public List<string>? Targets { get; set; }
    [JsonPropertyName("tgtSerials")] public List<uint>? TargetSerials { get; set; }
    [JsonPropertyName("tgtLabels")] public List<string>? TargetLabels { get; set; }
    [JsonPropertyName("reread")] public bool? Reread { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("method")] public string? Method { get; set; }
    [JsonPropertyName("verify")] public bool? Verify { get; set; }
    [JsonPropertyName("ids")] public bool? CompareIds { get; set; }
    [JsonPropertyName("rel")] public string? Rel { get; set; }
    [JsonPropertyName("grp")] public string? Group { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
    [JsonPropertyName("ctime")] public long? CreationTime { get; set; }
    [JsonPropertyName("mtime")] public long? LastWriteTime { get; set; }
    [JsonPropertyName("attr")] public int? Attributes { get; set; }
    [JsonPropertyName("side")] public string? Side { get; set; }
    [JsonPropertyName("why")] public string? Why { get; set; }
    [JsonPropertyName("fid")] public string? FileId { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("xxh64")] public string? Xxh64 { get; set; }
    [JsonPropertyName("tmp")] public string? Temp { get; set; }
    [JsonPropertyName("ads")] public int? Streams { get; set; }
    [JsonPropertyName("how")] public string? How { get; set; }
    [JsonPropertyName("err")] public string? Error { get; set; }
    [JsonPropertyName("what")] public string? What { get; set; }
    [JsonPropertyName("journal")] public string? JournalFile { get; set; }
    [JsonPropertyName("count")] public int? Count { get; set; }
    [JsonPropertyName("bytes")] public long? Bytes { get; set; }
    [JsonPropertyName("done")] public int? Done { get; set; }
    [JsonPropertyName("skipped")] public int? Skipped { get; set; }
    [JsonPropertyName("failed")] public int? Failed { get; set; }
    [JsonPropertyName("at")] public string? At { get; set; }
    /// <summary>The record rescan: what should move and is still in the source, beyond the job's own record.</summary>
    [JsonPropertyName("left")] public List<RescanFile>? Left { get; set; }
    /// <summary>The record rescan: folders of the source that could not be read.</summary>
    [JsonPropertyName("unreadable")] public List<string>? Unreadable { get; set; }

    public static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");
}

/// <param name="inUse">The job log is open for writing somewhere else (another window or a command-line run).</param>
public sealed class JournalException(string message, Exception? inner = null, bool inUse = false) : Exception(message, inner)
{
    public bool InUse { get; } = inUse;
}

public sealed class JournalWriter : IDisposable
{
    public const string InUseMessage =
        "This job is open in another IVAR Offload window (or in a command-line run). Wait until the job is finished there. Then try again.";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly FileStream _stream;

    public string Path { get; }

    private JournalWriter(FileStream stream, string path)
    {
        _stream = stream;
        Path = path;
    }

    /// <summary>Creates a new journal. Other processes can read it but not write while it is open.</summary>
    public static JournalWriter CreateNew(string path) =>
        new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1), path);

    /// <summary>
    /// Opens an existing journal to continue it. The exclusive write lock is taken first; only then is the journal
    /// read, so <paramref name="state"/> includes everything any other process wrote, and only a torn last line
    /// found at that moment is cut off.
    /// </summary>
    /// <exception cref="JournalException">With <see cref="JournalException.InUse"/> set when another process has it open.</exception>
    public static JournalWriter OpenForAppend(string path, out JobState state)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 1);
        }
        catch (IOException e) when ((e.HResult & 0xFFFF) is 32 or 33)
        {
            throw new JournalException(InUseMessage, e, inUse: true);
        }
        try
        {
            state = JournalReader.Read(path);
            if (stream.Length != state.ValidLength) stream.SetLength(state.ValidLength);
            stream.Seek(0, SeekOrigin.End);
            return new JournalWriter(stream, path);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Write(JournalRecord record, bool flush = true)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        byte[] line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        try
        {
            _stream.Write(line);
            if (flush) _stream.Flush(flushToDisk: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            throw new JournalException($"A write to the job log ({Path}) failed: {e.Message}", e);
        }
    }

    public void Dispose() => _stream.Dispose();
}

public enum ItemStage { Pending, Prepared, Copying, Copied, Placed, Done, Skipped }

public sealed class JobItem
{
    public required int Index { get; init; }
    public required string Rel { get; init; }
    public required long Size { get; init; }
    public required long CreationTime { get; init; }
    public required long LastWriteTime { get; init; }
    public required string Why { get; init; }
    /// <summary>Files that stay together (a clip with its companions). Empty: the file is a group of its own.</summary>
    public string Group { get; init; } = "";
    /// <summary>
    /// Undo jobs: the SHA-256 the file had when it was sorted. The file only goes back if it still has it. Backup top-ups:
    /// the SHA-256 the earlier backup verified for this file (null for a file that is new on the card).
    /// </summary>
    public string? ExpectedSha256 { get; init; }
    /// <summary>Backup top-ups: the xxHash64 the earlier backup recorded with <see cref="ExpectedSha256"/>.</summary>
    public string? ExpectedXxh64 { get; init; }
    public ItemStage Stage { get; set; }
    public string? FileId { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>Backup jobs: the xxHash64 of the content (for the ASC MHL manifest), recorded with the SHA-256.</summary>
    public string? Xxh64 { get; set; }
    public string? Temp { get; set; }
    /// <summary>Times of the file as it was when its move started (from the pre/copy record); null before that.</summary>
    public long? SeenCreationTime { get; set; }
    public long? SeenLastWriteTime { get; set; }
    /// <summary>The attributes a file had before it was renamed (from the pre record; a rename sets Archive); null when not recorded.</summary>
    public FileAttributes? SeenAttributes { get; set; }
    /// <summary>Relative path of a damaged copy that was renamed out of the way in the target.</summary>
    public string? SetAside { get; set; }
    /// <summary>Backup top-ups: why the file that was in the backup folder was moved aside (see BackupReasons).</summary>
    public string? SetAsideWhy { get; set; }
    /// <summary>
    /// Backup top-ups: the earlier version of a file changed on the card, kept next to the new one (<see cref="SetAside"/>
    /// is then in the backup folder, not in its log folder): what it was when it was moved there.
    /// </summary>
    public KeptBeside? Beside { get; set; }
    /// <summary>
    /// Backup top-ups: the file in the backup folder under this name is to be replaced by the card's (and why) - moved
    /// aside once the card's copy is verified, never before.
    /// </summary>
    public string? ReplaceWhy { get; set; }
    public string? How { get; set; }
    /// <summary>Skip reason or last error.</summary>
    public string? Note { get; set; }
    public bool Failed { get; set; }
    public string? At { get; set; }

    public bool IsFinished => Stage is ItemStage.Done or ItemStage.Skipped;

    /// <summary>
    /// Not in the target: not moved, and not skipped because an identical copy was already there. A file that
    /// disappeared from the source before it was moved (<see cref="IsMissing"/>) is not in the source either.
    /// </summary>
    public bool IsStillInSource => Stage != ItemStage.Done && !(Stage == ItemStage.Skipped && SkipReasons.IsIdenticalInTarget(Note)) && !IsMissing;

    /// <summary>
    /// The file disappeared from the source (something else removed it) before it was moved, so it is in neither
    /// folder - unless a copy was kept in the target under another name (<see cref="SetAside"/>), which the failure
    /// or the reports then name.
    /// </summary>
    public bool IsMissing => Stage switch
    {
        ItemStage.Done => false,
        ItemStage.Skipped => Note == SkipReasons.SourceGone,
        _ => Failed && FailReasons.IsOriginalGone(Note),
    };
}

/// <summary>The earlier version of a file kept next to the new one by a backup top-up, as it was read when it was renamed.</summary>
public sealed record KeptBeside(long Size, long CreationTime, long LastWriteTime, string Sha256, string Xxh64);

/// <summary>
/// A file of the moving side that the preview kept in the source (see MovePlan.HeldBack): it was never part of the run,
/// but it is still in the source and the job's reports say so.
/// </summary>
public sealed record HeldFile(string Rel, long Size, long LastWriteTime, string Why);

/// <summary>A file the check after a sort found in the source that should move and was not part of the job (see <see cref="RescanRecord"/>).</summary>
/// <param name="Why">Why it is still there, in plain words (<see cref="SourceCheck"/>'s reasons).</param>
public sealed record RescanFile(
    [property: JsonPropertyName("rel")] string Rel,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("why")] string Why);

/// <summary>
/// What the check of the source after an ended sort found (the record "rescan", written by <see cref="SourceCheck.Record"/>):
/// the files that should move and were not part of the job (added after the preview, online-only, left by an earlier
/// sort, held back by a name clash now), folders it could not read, or why it could not check at all. The last one wins.
/// </summary>
/// <param name="NotChecked">Why the source could not be scanned again; null when it was.</param>
/// <param name="MovableNow">How many files a new job could move right away.</param>
public sealed record RescanRecord(string? At, string? NotChecked, IReadOnlyList<string> Unreadable, IReadOnlyList<RescanFile> Left, int MovableNow);

public enum JobKind
{
    /// <summary>Moves videos or photos out of a source folder.</summary>
    Sort,
    /// <summary>Moves the files of an earlier sort back to where they came from.</summary>
    Undo,
    /// <summary>Copies a card to one or more destinations (one log per destination). Never run by the sort engine.</summary>
    Backup,
}

public sealed class JobHeader
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public required MoveMode Mode { get; init; }
    public required TransferMethod Method { get; init; }
    public required bool Verify { get; init; }
    public required bool CompareIds { get; init; }
    public int Version { get; init; } = 1;
    public JobKind Kind { get; init; }
    /// <summary>Undo jobs: the journal of the sort job being undone, and its id.</summary>
    public string? UndoesJournal { get; init; }
    public string? UndoesId { get; init; }
    /// <summary>Backup top-ups: the job id of the earlier backup whose folder this backup adds the card's new files to.</summary>
    public string? AddsTo { get; init; }
    /// <summary>Volume serial numbers when the job was created (0 = unknown, e.g. a version 1 journal).</summary>
    public uint SourceSerial { get; init; }
    public uint TargetSerial { get; init; }
    public string SourceLabel { get; init; } = "";
    public string TargetLabel { get; init; } = "";
    /// <summary>
    /// Where the source really was when the job was made, when it was named through a subst drive letter ("C:\Shoots\Card1"
    /// for "W:\Card1"; null otherwise). <see cref="SourceSerial"/> is that folder's drive. A drive letter change is looked
    /// for from it, never from the subst letter: below another letter lies another folder than below the subst's.
    /// </summary>
    public string? SourceReal { get; init; }
    /// <summary>Where the target really was, when it was named through a subst drive letter (see <see cref="SourceReal"/>).</summary>
    public string? TargetReal { get; init; }
    /// <summary>Backup jobs: every destination folder of the backup (this log's own included), and their volume serials.</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];
    public IReadOnlyList<uint> TargetSerials { get; init; } = [];
    public IReadOnlyList<string> TargetLabels { get; init; } = [];
    /// <summary>Backup jobs: each card file is read a second time and must match before its copies count as verified.</summary>
    public bool Reread { get; init; }
    /// <summary>The files and bytes the job planned, as its header recorded them (null when not recorded).</summary>
    public int? Count { get; init; }
    public long? Bytes { get; init; }
    public string? Created { get; init; }
    public string? Tool { get; init; }
    public string? Machine { get; init; }
    public string? User { get; init; }
}

/// <summary>An undo job created for a sort job. Status is "started" until the undo job ends ("completed" or "closed").</summary>
/// <param name="NotMovedBack">Files of the sort that the undo did not move back (null when not recorded, e.g. by older versions).</param>
public sealed record UndoMark(string JobId, string JournalPath, string? At, string Status, int? NotMovedBack = null)
{
    public bool IsFinished => Status is "completed" or "closed";

    /// <summary>The undo ended, but some files of the sort were not moved back: a new undo can try the rest.</summary>
    public bool IsPartial => Status == "closed" || (Status == "completed" && NotMovedBack > 0);
}

/// <summary>The state of a job as reconstructed from its journal.</summary>
public sealed class JobState
{
    public required string JournalPath { get; init; }
    public required JobHeader Header { get; init; }
    public List<JobItem> Items { get; } = [];
    public Dictionary<string, FolderTimes> SourceFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CreatedFolders { get; } = [];
    public List<string> RemovedFolders { get; } = [];
    /// <summary>Files the preview kept in the source (never part of the run); see <see cref="HeldFile"/>.</summary>
    public List<HeldFile> HeldBack { get; } = [];
    public bool PlanComplete { get; set; }
    public JournalRecord? End { get; set; }
    /// <summary>Sort jobs: the undo job created for this job (the last one, if an earlier undo was closed early).</summary>
    public UndoMark? UndoneBy { get; set; }
    /// <summary>Backup jobs: the ASC MHL generation file written into the destination (relative path), once written.</summary>
    public string? Mhl { get; set; }
    /// <summary>Backup jobs: why no ASC MHL generation was written (the card's own history could not be read); null otherwise.</summary>
    public string? MhlSkipped { get; set; }
    /// <summary>Backup jobs: the card no longer matched the checksums its own ASC MHL history recorded.</summary>
    public bool MhlMismatch { get; set; }
    /// <summary>
    /// Backup top-ups: where the folder's earlier ASC MHL history was moved (relative path) before a new one was started,
    /// because files it lists were changed or are gone; null when it was continued.
    /// </summary>
    public string? MhlRestarted { get; set; }
    /// <summary>Sort jobs: what the last check of the source after the job ended found (null when it was never checked).</summary>
    public RescanRecord? Rescan { get; set; }
    /// <summary>Length of the journal up to the last complete, valid line.</summary>
    public long ValidLength { get; set; }

    public int DoneCount => Items.Count(i => i.Stage == ItemStage.Done);
    public int SkippedCount => Items.Count(i => i.Stage == ItemStage.Skipped);
    public int FailedCount => Items.Count(i => i.Failed && !i.IsFinished);
    public int PendingCount => Items.Count(i => !i.IsFinished);
    public long BytesTotal => Items.Sum(i => i.Size);
    public long BytesDone => Items.Where(i => i.Stage == ItemStage.Done).Sum(i => i.Size);
    public bool IsEnded => End is not null;
    public bool IsResumable => PlanComplete && End is null;
    public bool IsUndo => Header.Kind == JobKind.Undo;
    public bool IsBackup => Header.Kind == JobKind.Backup;
    /// <summary>A backup that adds a card's new files to its earlier backup (see <see cref="JobHeader.AddsTo"/>).</summary>
    public bool IsTopUp => IsBackup && Header.AddsTo is not null;

    /// <summary>Items skipped because an identical copy was already in the target (nothing is lost by leaving them).</summary>
    public int IdenticalInTargetCount => Items.Count(i => i.Stage == ItemStage.Skipped && SkipReasons.IsIdenticalInTarget(i.Note));

    /// <summary>
    /// Planned files that are not in the target: every item not moved, except identical copies already there and
    /// files that disappeared from the source (<see cref="Missing"/>). Files the preview held back are in <see cref="HeldBack"/>.
    /// </summary>
    public IEnumerable<JobItem> StillInSource => Items.Where(i => i.IsStillInSource);

    /// <summary>
    /// Everything of the moving side that is still in the source because of this job: the planned files that did not
    /// move (<see cref="StillInSource"/>) plus the files the preview held back (<see cref="HeldBack"/>).
    /// </summary>
    public int StillInSourceCount => Items.Count(i => i.IsStillInSource) + HeldBack.Count;
    public long StillInSourceBytes => Items.Where(i => i.IsStillInSource).Sum(i => i.Size) + HeldBack.Sum(h => h.Size);

    /// <summary>Planned files that disappeared from the source before they were moved: they are in neither folder.</summary>
    public IEnumerable<JobItem> Missing => Items.Where(i => i.IsMissing);
    public int MissingCount => Items.Count(i => i.IsMissing);
}

public static class JournalReader
{
    public static JobState Read(string path)
    {
        byte[] data;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            data = new byte[stream.Length];
            stream.ReadExactly(data);
        }

        JobState? state = null;
        int position = 0;
        long validLength = 0;
        int lineNumber = 0;
        while (position < data.Length)
        {
            int newline = Array.IndexOf(data, (byte)'\n', position);
            if (newline < 0) break; // torn last line from a crash: ignored and later truncated
            lineNumber++;
            var line = new ReadOnlySpan<byte>(data, position, newline - position);
            JournalRecord? record = TryParse(line);
            if (record is null)
            {
                if (IsGarbageTail(data, position)) break; // e.g. zero-filled tail after a power cut
                throw new JournalException($"The job log is damaged at line {lineNumber}: {path}");
            }
            if (state is null)
            {
                if (record.Type != "job") throw new JournalException($"This file is not an IVAR Offload job log: {path}");
                state = new JobState { JournalPath = path, Header = ToHeader(record) };
            }
            else
                Apply(state, record, path);
            position = newline + 1;
            validLength = position;
        }
        if (state is null) throw new JournalException($"The job log is empty or unreadable: {path}");
        state.ValidLength = validLength;
        return state;
    }

    /// <summary>Reads only the header of a journal (fast, used to list jobs).</summary>
    public static JobHeader? TryReadHeader(string path)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
            string? first = reader.ReadLine();
            JournalRecord? record = first is null ? null : TryParse(Encoding.UTF8.GetBytes(first));
            return record?.Type == "job" ? ToHeader(record) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException or ArgumentException)
        {
            return null;
        }
    }

    private static JournalRecord? TryParse(ReadOnlySpan<byte> line)
    {
        try
        {
            return JsonSerializer.Deserialize<JournalRecord>(line, JournalWriter.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsGarbageTail(byte[] data, int from)
    {
        // A tail is garbage when no complete valid record follows it.
        int position = from;
        while (position < data.Length)
        {
            int newline = Array.IndexOf(data, (byte)'\n', position);
            if (newline < 0) return true;
            if (TryParse(new ReadOnlySpan<byte>(data, position, newline - position)) is not null) return false;
            position = newline + 1;
        }
        return true;
    }

    private static JobHeader ToHeader(JournalRecord r) => new()
    {
        Id = r.JobId ?? "",
        Source = r.Source ?? throw new JournalException("The header of the job log has no source folder."),
        Target = r.Target ?? throw new JournalException("The header of the job log has no target folder."),
        Mode = Enum.Parse<MoveMode>(r.Mode ?? nameof(MoveMode.Videos), ignoreCase: true),
        Method = Enum.Parse<TransferMethod>(r.Method ?? nameof(TransferMethod.Copy), ignoreCase: true),
        Verify = r.Verify ?? true,
        CompareIds = r.CompareIds ?? false,
        Version = r.Version ?? 1,
        Kind = r.Kind?.ToLowerInvariant() switch
        {
            "undo" => JobKind.Undo,
            "backup" => JobKind.Backup,
            _ => JobKind.Sort,
        },
        Targets = r.Targets ?? [],
        TargetSerials = r.TargetSerials ?? [],
        TargetLabels = r.TargetLabels ?? [],
        Reread = r.Reread ?? false,
        Count = r.Count,
        Bytes = r.Bytes,
        UndoesJournal = r.Undoes,
        UndoesId = r.UndoesId,
        AddsTo = r.AddsTo,
        SourceSerial = r.SourceSerial ?? 0,
        TargetSerial = r.TargetSerial ?? 0,
        SourceLabel = r.SourceLabel ?? "",
        TargetLabel = r.TargetLabel ?? "",
        SourceReal = r.SourceReal,
        TargetReal = r.TargetReal,
        Created = r.At,
        Tool = r.Tool,
        Machine = r.Machine,
        User = r.User,
    };

    private static void Apply(JobState state, JournalRecord r, string path)
    {
        if (r.Type == "item")
        {
            if (r.Index != state.Items.Count) throw new JournalException($"The items in the job log are not in the correct order: {path}");
            state.Items.Add(new JobItem
            {
                Index = r.Index.Value,
                Rel = r.Rel ?? throw new JournalException($"Item {r.Index} in the job log has no path: {path}"),
                Size = r.Size ?? 0,
                CreationTime = r.CreationTime ?? 0,
                LastWriteTime = r.LastWriteTime ?? 0,
                Why = r.Why ?? "",
                Group = r.Group ?? "",
                ExpectedSha256 = r.Sha256,
                ExpectedXxh64 = r.Xxh64,
            });
            return;
        }

        JobItem? item = null;
        if (r.Index is int index)
        {
            if (index < 0 || index >= state.Items.Count) throw new JournalException($"The job log refers to item {index}, which does not exist: {path}");
            item = state.Items[index];
        }

        switch (r.Type)
        {
            case "dir" when r.Rel is not null:
                state.SourceFolders[r.Rel] = new FolderTimes(r.CreationTime ?? 0, r.LastWriteTime ?? 0);
                break;
            case "ready":
                state.PlanComplete = true;
                break;
            case "mkdir" when r.Rel is not null:
                state.CreatedFolders.Add(r.Rel);
                break;
            case "rmdir" when r.Rel is not null:
                state.RemovedFolders.Add(r.Rel);
                break;
            case "pre" when item is not null:
                item.Stage = ItemStage.Prepared;
                item.FileId = r.FileId;
                item.Sha256 = r.Sha256;
                item.SeenCreationTime = r.CreationTime;
                item.SeenLastWriteTime = r.LastWriteTime;
                item.SeenAttributes = (FileAttributes?)r.Attributes;
                item.Failed = false;
                break;
            case "copy" when item is not null:
                item.Stage = ItemStage.Copying;
                item.Temp = r.Temp;
                item.FileId = r.FileId;
                item.SeenCreationTime = r.CreationTime;
                item.SeenLastWriteTime = r.LastWriteTime;
                item.Failed = false;
                break;
            case "copied" when item is not null:
                item.Stage = ItemStage.Copied;
                item.Sha256 = r.Sha256;
                item.Xxh64 = r.Xxh64;
                break;
            case "placed" when item is not null:
                item.Stage = ItemStage.Placed;
                break;
            case "setaside" when item is not null:
                item.Stage = ItemStage.Pending;
                item.SetAside = r.Rel;
                item.SetAsideWhy = r.Why;
                item.Temp = null;
                item.Failed = false;
                break;
            case "done" when item is not null:
                item.Stage = ItemStage.Done;
                item.How = r.How;
                item.Sha256 = r.Sha256 ?? item.Sha256;
                item.Xxh64 = r.Xxh64 ?? item.Xxh64;
                item.At = r.At;
                item.Failed = false;
                item.Note = r.Why;
                break;
            case "skip" when item is not null:
                item.Stage = ItemStage.Skipped;
                item.Note = r.Why;
                item.At = r.At;
                item.Failed = false;
                break;
            case "fail" when item is not null:
                item.Failed = true;
                item.Note = r.Error;
                item.At = r.At;
                break;
            case "end":
                state.End = r;
                break;
            case "undo" when r.JobId is not null:
                state.UndoneBy = new UndoMark(r.JobId, r.JournalFile ?? "", r.At, r.What ?? "started", r.Skipped);
                break;
            case "replace" when item is not null:
                item.ReplaceWhy = r.Why;
                break;
            case "aside" when item is not null:
                item.SetAside = r.Rel;
                item.SetAsideWhy = r.Why;
                item.Beside = r.Sha256 is { } sha && r.Xxh64 is { } xxh ? new KeptBeside(r.Size ?? 0, r.CreationTime ?? 0, r.LastWriteTime ?? 0, sha, xxh) : null;
                break;
            case "mhl" when r.What == "restarted":
                state.MhlRestarted = r.Rel;
                break;
            case "mhl" when r.What == "skipped":
                state.MhlSkipped = r.Error ?? "";
                state.MhlMismatch = r.How == "mismatch";
                break;
            case "mhl" when r.Rel is not null:
                state.Mhl = r.Rel;
                break;
            case "held" when r.Rel is not null:
                state.HeldBack.Add(new HeldFile(r.Rel, r.Size ?? 0, r.LastWriteTime ?? 0, r.Why ?? ""));
                break;
            case "rescan":
                state.Rescan = new RescanRecord(r.At, r.Error, r.Unreadable ?? [], r.Left ?? [], r.Count ?? 0);
                break;
        }
    }
}
