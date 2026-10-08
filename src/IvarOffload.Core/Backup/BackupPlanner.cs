using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IvarOffload.Core.IO;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Core.Backup;

/// <summary>One destination of a backup: the folder the user picked, and the folder the copy goes into.</summary>
public sealed class BackupTarget
{
    /// <summary>The folder the user picked, e.g. E:\Cards.</summary>
    public required string Parent { get; init; }
    /// <summary>Where the copy goes: <see cref="Parent"/>\&lt;name&gt;, e.g. E:\Cards\260928_SONY_A.</summary>
    public required string Folder { get; init; }
    public VolumeInfo? Volume { get; init; }
    /// <summary>A top-up: the earlier backup in this destination that the card's new files are added to (<see cref="Folder"/> is its folder).</summary>
    public TopUpFolder? Earlier { get; init; }

    /// <summary>"E: T7 Shield", or "E:" when the drive has no label.</summary>
    public string DriveName => Volume is null ? Drives.Letter(Folder) : SourceGuards.DriveName(Volume);
}

/// <summary>Everything the Backup preview shows, and exactly what a confirmed backup will copy.</summary>
public sealed class BackupPlan
{
    public required BackupScan Scan { get; init; }
    public required string SourceRoot { get; init; }
    public VolumeInfo? SourceVolume { get; init; }
    /// <summary>The source looks like a memory card or camera drive.</summary>
    public bool IsCard { get; init; }
    /// <summary>"F: SONY_A" for a card or a drive root; the folder name otherwise.</summary>
    public required string SourceName { get; init; }
    /// <summary>The name of the folder the copy goes into on every destination.</summary>
    public required string Name { get; init; }
    public required IReadOnlyList<BackupTarget> Targets { get; init; }
    /// <summary>Each card file is read a second time; its copies only count as verified when both reads match.</summary>
    public bool Reread { get; init; }
    public required IReadOnlyList<PlanMessage> Messages { get; init; }
    /// <summary>ASC MHL generations already in the card's own history (its top-level ascmhl folder); 0 when it has none.</summary>
    public int MhlGenerations { get; init; }
    /// <summary>
    /// Not every copy will be on a drive of its own (two destinations share a drive, or one is on the drive being backed
    /// up): the one line the preview keeps next to Start. Null when every copy is on its own drive.
    /// </summary>
    public string? SameDriveNote { get; init; }
    /// <summary>Logs of unfinished backups found in the chosen destination folders (the app offers to resume them).</summary>
    public IReadOnlyList<string> UnfinishedJournals { get; init; } = [];
    /// <summary>
    /// An earlier backup of this card on every chosen destination that the card's new files can be added to (then the
    /// whole card is verified), or null. Found for a full backup too, so the preview can offer it.
    /// </summary>
    public TopUpOffer? TopUp { get; init; }
    /// <summary>This plan adds the card's new files to <see cref="TopUp"/> (in its folders) instead of making a new full backup.</summary>
    public bool IsTopUp { get; init; }
    /// <summary>Why an earlier backup of this card on the destinations can't be added to; null when there is none, or it can.</summary>
    public string? TopUpRefusal { get; init; }

    public IReadOnlyList<SourceFile> Files => Scan.Files;
    public long Bytes => Scan.Bytes;
    public bool HasErrors => Messages.Any(m => m.Level == MessageLevel.Error);
    public bool CanRun => !HasErrors && Files.Count > 0 && Targets.Count > 0;

    /// <summary>
    /// A rough duration: every byte is read from the card and written, then read back from every destination (and read
    /// from the card again when <see cref="Reread"/> is on) at about <paramref name="bytesPerSecond"/>.
    /// </summary>
    public TimeSpan Estimate(double bytesPerSecond = BackupPlanner.TypicalBytesPerSecond) =>
        TimeSpan.FromSeconds(Bytes * 2.0 / Math.Max(1, bytesPerSecond));
}

/// <summary>A connected drive that looks like a memory card or camera drive, for the list the user picks from.</summary>
/// <param name="Name">"F: SONY_A" (the drive letter and its label).</param>
/// <param name="Camera">The camera make the card's folders show ("Sony"), or null; see <see cref="CardCamera"/>.</param>
public sealed record ConnectedCard(string Root, string Name, string FileSystem, long UsedBytes, long TotalBytes, string? Camera = null);

public static partial class BackupPlanner
{
    /// <summary>In the warning for a destination on the drive being backed up (the app shows these as one line of its own).</summary>
    public const string SameDriveAsSourceMarker = "is on the same drive as the folder being backed up";

    /// <summary>In the warning for destinations that share a drive (the app shows these as one line of its own).</summary>
    public const string SharedDriveMarker = "destinations are on the same drive";

    /// <summary>
    /// Connected drives that look like memory cards or camera drives (a camera folder structure at the root on a FAT,
    /// exFAT or UDF drive; see <see cref="SourceGuards.MemoryCardNote"/>). Never the Windows drive. Never throws.
    /// </summary>
    public static List<ConnectedCard> ConnectedCards()
    {
        var cards = new List<ConnectedCard>();
        string system = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return cards;
        }
        foreach (DriveInfo d in drives)
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Removable or DriveType.Fixed or DriveType.CDRom)) continue;
                if (string.Equals(d.Name, system, StringComparison.OrdinalIgnoreCase)) continue;
                if (SourceGuards.MemoryCardNote(d.Name) is null) continue;
                VolumeInfo v = VolumeInfo.Of(d.Name);
                cards.Add(new ConnectedCard(d.Name, SourceGuards.DriveName(v), v.FileSystem, v.TotalBytes - v.FreeBytes, v.TotalBytes,
                    CardCamera.Of(d.Name, DriveFacts.SubfolderNames, DriveFacts.FileNames)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A drive that stops answering while it is looked at is simply not listed.
            }
        }
        return cards;
    }

    /// <summary>At most this many destinations per backup.</summary>
    public const int MaxTargets = 3;

    /// <summary>What a good USB card reader delivers; only used for the time estimate in the preview.</summary>
    public const double TypicalBytesPerSecond = 150e6;

    /// <param name="topUp">
    /// Add the card's new files to the earlier backup of this card that every destination holds (<see cref="BackupPlan.TopUp"/>)
    /// and verify the whole card, instead of making a new full backup into a folder named <paramref name="name"/>.
    /// </param>
    public static BackupPlan Build(BackupScan scan, IReadOnlyList<string> destinations, string name, bool reread = true, bool topUp = false) =>
        Build(scan, destinations, name, reread, PlanEnvironment.Default, topUp);

    internal static BackupPlan Build(BackupScan scan, IReadOnlyList<string> destinations, string name, bool reread, PlanEnvironment env, bool topUp = false)
    {
        var messages = new List<PlanMessage>();
        string source = scan.SourceRoot;
        VolumeInfo? sourceVolume = null;
        try
        {
            sourceVolume = env.VolumeOf(source);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            messages.Add(new PlanMessage(MessageLevel.Error, $"Cannot read the card or drive: {e.Message}"));
        }

        bool isCard = false;
        string sourceName = SourceName(source, sourceVolume);
        if (sourceVolume is not null)
        {
            IReadOnlyList<string> signs = SourceGuards.CardSignsAtRoot(sourceVolume.Root, env.SubfolderNames, env.FileNames);
            isCard = SourceGuards.CheckMemoryCard(source, sourceVolume, signs) is not null;
        }
        if (!isCard && env.TestCard is { } testCard)
        {
            isCard = true;
            sourceName = testCard;
        }

        if (scan.Files.Count == 0)
            messages.Add(new PlanMessage(MessageLevel.Error, "Nothing to back up: there are no files in this folder."));
        AddScanFindings(scan, messages);

        // An earlier backup of this card on every destination, that the card's new files can be added to.
        var parents = destinations.Take(MaxTargets).Select(FullPathOrNull).OfType<string>().ToList();
        TopUpOffer? offer = null;
        string? refusal = null;
        if (destinations.Count is > 0 and <= MaxTargets && parents.Count == destinations.Count)
            offer = EarlierBackups.FindTopUp(scan, sourceVolume?.SerialNumber ?? 0, parents, out refusal);
        if (topUp && offer is null)
            messages.Add(new PlanMessage(MessageLevel.Error, refusal
                ?? "There is no earlier backup of this card in the chosen destination folders to add new files to. Make a new backup instead."));
        bool adding = topUp && offer is not null;

        string? nameError = adding ? null : ValidateName(name);
        if (nameError is not null) messages.Add(new PlanMessage(MessageLevel.Error, nameError));

        var targets = new List<BackupTarget>();
        var unfinished = new List<string>();
        var looked = new List<string>(); // every backup folder of a destination that could be looked at, for earlier backups next to it
        if (destinations.Count == 0) messages.Add(new PlanMessage(MessageLevel.Error, "Choose a destination for the backup."));
        if (destinations.Count > MaxTargets)
            messages.Add(new PlanMessage(MessageLevel.Error, $"A backup can go to at most {MaxTargets} destinations."));
        int k = -1;
        foreach (string parent in destinations.Take(MaxTargets))
        {
            k++;
            TopUpFolder? earlier = adding ? offer!.Folders[k] : null;
            string folder;
            string? unfinishedJournal = null;
            string? error = earlier is not null
                ? ValidateTopUpFolder(source, earlier.Folder, env.RealPathOf, out folder)
                : ValidateDestination(source, sourceVolume?.SerialNumber ?? 0, parent, nameError is null ? name : "", env.RealPathOf,
                    out folder, out unfinishedJournal);
            if (unfinishedJournal is not null) unfinished.Add(unfinishedJournal);
            if (folder.Length > 0) looked.Add(folder);
            if (error is not null)
            {
                messages.Add(new PlanMessage(MessageLevel.Error, error));
                continue;
            }
            VolumeInfo? volume = null;
            try
            {
                volume = env.VolumeOf(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                messages.Add(new PlanMessage(MessageLevel.Error, $"Cannot read the destination drive of {folder}: {e.Message}"));
            }
            if (targets.FirstOrDefault(t => SameFolder(t.Folder, folder, env.RealPathOf)) is { } twice)
            {
                messages.Add(new PlanMessage(MessageLevel.Error, $"The same destination is chosen twice: {twice.Folder}."));
                continue;
            }
            targets.Add(new BackupTarget
            {
                Parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent.Trim().Trim('"'))),
                Folder = folder,
                Volume = volume,
                Earlier = earlier,
            });
        }

        string? sameDrive = null;
        foreach (BackupTarget t in targets)
        {
            if (t.Volume is null) continue;
            if (sourceVolume is not null && t.Volume.IsSameVolume(sourceVolume) && !isCard)
                sameDrive ??= $"Not a separate copy: {t.DriveName} is the drive being backed up.";
            if (sourceVolume is not null && t.Volume.IsSameVolume(sourceVolume))
                messages.Add(isCard
                    ? new PlanMessage(MessageLevel.Error, $"{t.Folder} is on the card itself. A backup never writes to the card: choose a folder on another drive.")
                    : new PlanMessage(MessageLevel.Warning, $"{t.Folder} {SameDriveAsSourceMarker} ({t.DriveName}). "
                        + "It is not a separate copy: if that drive fails, both are lost."));
            string? sync = env.SyncOf(t.Folder);
            if (sync is not null)
                messages.Add(new PlanMessage(MessageLevel.Info, $"{t.Folder} is synchronized by {sync}: the backup will be uploaded from there too."));
        }
        CheckSpace(targets, t => t.Earlier?.ToCopy ?? scan.Files, env, messages);
        messages.AddRange(EarlierBackups.Check(scan, sourceVolume?.SerialNumber ?? 0, looked.Select(f => Path.GetDirectoryName(f)!),
            targets.Select(t => t.Folder).ToList(), unfinished, skipJob: offer?.JobId));
        if (refusal is not null && !topUp) messages.Add(new PlanMessage(MessageLevel.Info, refusal));
        if (adding) AddTopUpWarnings(offer!, messages);
        foreach (var shared in targets.Where(t => t.Volume is not null).GroupBy(t => (t.Volume!.SerialNumber, t.Volume.Root.ToUpperInvariant())).Where(g => g.Count() > 1))
        {
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"{shared.Count()} {SharedDriveMarker} ({shared.First().DriveName}). They are not separate copies: if that drive fails, all of them are lost."));
            sameDrive ??= $"Not separate copies: {shared.Count()} destinations are on the same drive ({shared.First().DriveName}).";
        }

        int generations = AscMhl.GenerationsIn(source);
        if (generations > 0)
            messages.Add(new PlanMessage(MessageLevel.Info,
                $"The card has an ASC MHL checksum history ({generations} generation{(generations == 1 ? "" : "s")}). It is copied, and the backup adds its own generation to it on every destination."));
        if (!reread)
            messages.Add(new PlanMessage(MessageLevel.Info,
                "The second read of the card is off: every copy is still read back from its destination and compared, but a card or reader that returns wrong data once may go unnoticed."));

        return new BackupPlan
        {
            Scan = scan,
            SourceRoot = source,
            SourceVolume = sourceVolume,
            IsCard = isCard,
            SourceName = sourceName,
            Name = adding && targets.Count > 0 ? Path.GetFileName(targets[0].Folder) : name,
            Targets = targets,
            Reread = reread,
            Messages = messages.OrderByDescending(m => m.Level).ToList(),
            MhlGenerations = generations,
            SameDriveNote = sameDrive,
            UnfinishedJournals = unfinished,
            TopUp = offer,
            IsTopUp = adding,
            TopUpRefusal = refusal,
        };
    }

    /// <summary>What a top-up does that the user should know before it starts, beyond adding new files.</summary>
    private static void AddTopUpWarnings(TopUpOffer offer, List<PlanMessage> messages)
    {
        string replaced = offer.ReplacedFolder;
        if (offer.ChangedFiles.Count > 0)
        {
            int n = offer.ChangedFiles.Count;
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"{(n == 1 ? "1 file" : $"{n:N0} files")} on the card {(n == 1 ? "has" : "have")} the name of a file in the backup but another size or date "
                + $"(e.g. {offer.ChangedFiles[0]}): a different shot whose number the camera used again after a deletion, or a file edited in the camera. "
                + $"The backup's version stays next to the card's, renamed \"{Path.GetFileNameWithoutExtension(offer.ChangedFiles[0])} (earlier){Path.GetExtension(offer.ChangedFiles[0])}\"."));
        }
        if (offer.Missing > 0)
        {
            TopUpFolder most = offer.Folders.MaxBy(f => f.Missing)!;
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"{(offer.Missing == 1 ? "1 file" : $"{offer.Missing:N0} files")} ({Format.Bytes(most.MissingBytes)}) of the backup {(offer.Missing == 1 ? "is" : "are")} no longer in "
                + $"{most.Folder} (moved out by a sort, or deleted): {(offer.Missing == 1 ? "it is" : "they are")} copied into it again from the card, so the folder "
                + "holds the whole card again. Files a sort moved out are then in both places."));
        }
        if (offer.EditedInFolder > 0)
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"{(offer.EditedInFolder == 1 ? "1 file" : $"{offer.EditedInFolder:N0} files")} in the backup folder changed there since the earlier backup "
                + $"(edited by another program?): the card's version is copied in {(offer.EditedInFolder == 1 ? "its" : "their")} place, and the edited "
                + $"{(offer.EditedInFolder == 1 ? "one is" : "ones are")} kept in {replaced}."));
    }

    private static string? FullPathOrNull(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>An error for an earlier backup's folder that a top-up can't add to, or null. Outputs the folder.</summary>
    private static string? ValidateTopUpFolder(string source, string earlier, Func<string, string> realPath, out string folder)
    {
        folder = earlier;
        string realSource = realPath(source), realFolder = realPath(folder);
        if (Planner.IsInside(folder, source) || Planner.IsInside(realFolder, realSource))
            return $"The earlier backup {folder} is inside the folder being backed up. Choose a folder on another drive.";
        if (Planner.IsInside(source, folder) || Planner.IsInside(realSource, realFolder))
            return $"The folder being backed up is inside the earlier backup {folder}. Choose another destination.";
        return Directory.Exists(folder) ? null : $"The earlier backup {folder} is not there any more. Scan again.";
    }

    /// <summary>
    /// "260928_SONY_A": the <paramref name="template"/> (<see cref="DefaultTemplate"/> when null or not valid) filled in
    /// with the date and time <paramref name="now"/>, the card's label (or the folder's name; "Card" when it has none) and
    /// the camera make. When that folder already holds something in one of <paramref name="destinations"/> (the second
    /// card of a camera that labels every card alike, the same day), "_2", "_3", ... is added, so the name is free everywhere.
    /// A <paramref name="description"/> ("KebnekaiseFlight") is added after the name, with "_".
    /// </summary>
    public static string DefaultName(string source, DateTime now, IEnumerable<string>? destinations = null, string? template = null, string? description = null)
    {
        string first = WithDescription(DefaultName(source, now, template is not null && ValidateTemplate(template) is null ? template : DefaultTemplate), description);
        string name = first;
        var parents = (destinations ?? []).Select(d => d.Trim().Trim('"')).Where(d => d.Length > 0).ToList();
        for (int n = 2; n < 1000 && parents.Any(p => Taken(p, name)); n++)
            name = $"{first}_{n}";
        return name;
    }

    /// <summary>
    /// "260930_MAVIC_KebnekaiseFlight": a name with a description added, without the characters a folder name can't
    /// hold. The name alone when there is no description.
    /// </summary>
    public static string WithDescription(string name, string? description)
    {
        string clean = new string((description ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? name : name.Length == 0 ? clean : $"{name}_{clean}";
    }

    /// <summary>The folder name pattern used unless the user sets another: the date as YYMMDD and the card's label.</summary>
    public const string DefaultTemplate = "{YYMMDD}_{card}";

    /// <summary>What a folder name pattern can hold, and what each part becomes.</summary>
    public static readonly IReadOnlyList<(string Token, string Meaning)> TemplateTokens =
    [
        ("{card}", "the card's label (its name in Windows), or the folder's name"),
        ("{camera}", "the camera make, e.g. Sony (left out when the card doesn't show it)"),
        ("{YYMMDD}", "the date, as 260928"),
        ("{HHMMSS}", "the time, as 143015"),
        ("{...}", "any mix of YYYY or YY (year), MM (month), DD (day), HH (hour), MM (minutes, after HH or before SS) and SS "
            + "(seconds), with - _ . or a space between them, e.g. {YYYY-MM-DD} or {YYMMDD_HHMMSS}"),
    ];

    /// <summary>
    /// An earlier pattern with the names used before beta 6 ({date}, {year}, {month}, {day}) in date and time codes, e.g.
    /// "{date}_{camera}_{card}" becomes "{YYYY-MM-DD}_{camera}_{card}" (the same names as before). Other patterns are
    /// returned as they are. <paramref name="saved"/>: the pattern was remembered by the app, which saved its default too,
    /// so the old default {date}_{card} becomes today's <see cref="DefaultTemplate"/>.
    /// </summary>
    public static string UpgradeTemplate(string template, bool saved = false)
    {
        string t = template.Trim();
        if (saved && t.Equals("{date}_{card}", StringComparison.OrdinalIgnoreCase)) return DefaultTemplate;
        return LegacyToken().Replace(t, m => m.Value.ToLowerInvariant() switch
        {
            "{date}" => "{YYYY-MM-DD}",
            "{year}" => "{YYYY}",
            "{month}" => "{MM}",
            _ => "{DD}",
        });
    }

    /// <summary>An error for a folder name pattern that cannot be used, or null.</summary>
    public static string? ValidateTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "Enter a pattern for the folder name, e.g. " + DefaultTemplate + ".";
        foreach (Match m in TemplateToken().Matches(template))
            if (!IsNameToken(m.Value) && DateTimeCodes(m.Value) is null)
                return $"{m.Value} is not known. Use {{card}}, {{camera}}, or date and time codes such as {{YYMMDD}} or {{HHMMSS}} "
                    + "(YYYY, YY, MM, DD, HH, SS).";
        string rest = TemplateToken().Replace(template, "");
        if (rest.IndexOfAny(['{', '}']) >= 0) return "A { or } in the pattern is not part of a known name such as {card}.";
        // Checked with and without a camera make: most cards don't show one.
        foreach (string? camera in new[] { "Sony", null })
        {
            string name = ExpandTemplate(template, new DateTime(2026, 9, 28, 14, 30, 15), "SONY_A", camera);
            if (name.Length == 0) return "Add a date such as {YYMMDD}, or {card}, to the pattern: a card that doesn't show its camera make would get no name.";
            if (ValidateName(name) is { } error) return error.Replace("The backup folder name", "The pattern", StringComparison.Ordinal);
        }
        return null;
    }

    /// <summary>
    /// The folder name a pattern gives, e.g. "{YYMMDD}_{camera}_{card}" gives "260928_Sony_SONY_A". A part that is
    /// empty (no camera make) is left out together with the separator ("_", "-" or a space) before it.
    /// </summary>
    public static string ExpandTemplate(string template, DateTime date, string card, string? camera)
    {
        var name = new StringBuilder();
        int position = 0;
        bool dropSeparator = false;
        foreach (Match m in TemplateToken().Matches(template))
        {
            AppendLiteral(template[position..m.Index]);
            string value = m.Value.ToLowerInvariant() switch
            {
                "{card}" => card,
                "{camera}" => Sanitize(camera ?? ""),
                _ => DateTimeCodes(m.Value) is { } format ? date.ToString(format, CultureInfo.InvariantCulture) : m.Value,
            };
            if (value.Length == 0)
            {
                // "{YYMMDD}_{camera}_{card}" without a make: "260928_SONY_A", not "260928__SONY_A".
                if (name.Length > 0 && IsSeparator(name[^1])) name.Length--;
                else if (name.Length == 0) dropSeparator = true;
            }
            else dropSeparator = false;
            name.Append(value);
            position = m.Index + m.Length;
        }
        AppendLiteral(template[position..]);
        return name.ToString().Trim();

        void AppendLiteral(string text)
        {
            if (dropSeparator && text.Length > 0)
            {
                if (IsSeparator(text[0])) text = text[1..];
                dropSeparator = false;
            }
            name.Append(text);
        }
    }

    private static bool IsNameToken(string token) =>
        token.Equals("{card}", StringComparison.OrdinalIgnoreCase) || token.Equals("{camera}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The .NET date format for a token made of date and time codes, such as "{YYMMDD_HHMMSS}" ("yyMMdd_HHmmss"), or
    /// null. Codes: YYYY or YY, MM, DD, HH, SS, in upper or lower case, with - _ . or a space between them. MM is the
    /// month, except right after HH or right before SS (with or without a separator between), where it is the minutes.
    /// </summary>
    internal static string? DateTimeCodes(string token)
    {
        if (DateTimeToken().Match(token) is not { Success: true } m) return null;
        var codes = m.Groups[1].Captures.Select(c => c.Value.ToUpperInvariant()).ToList();
        var format = new StringBuilder();
        for (int i = 0; i < codes.Count; i++)
        {
            string code = codes[i];
            if (code is "-" or "_" or "." or " ")
            {
                format.Append('\'').Append(code).Append('\'');
                continue;
            }
            string? before = codes.Take(i).LastOrDefault(c => c.Length > 1);
            string? after = codes.Skip(i + 1).FirstOrDefault(c => c.Length > 1);
            format.Append(code switch
            {
                "YYYY" => "yyyy",
                "YY" => "yy",
                "MM" => before == "HH" || after == "SS" ? "mm" : "MM",
                "DD" => "dd",
                "HH" => "HH",
                _ => "ss",
            });
        }
        return format.ToString();
    }

    /// <summary>"{" date and time codes (YYYY, YY, MM, DD, HH, SS) with separators "}", at least one code.</summary>
    [GeneratedRegex(@"^\{(?=[^}]*[a-z])(YYYY|YY|MM|DD|HH|SS|[-_. ])+\}$", RegexOptions.IgnoreCase)]
    private static partial Regex DateTimeToken();

    [GeneratedRegex(@"\{(date|year|month|day)\}", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyToken();

    private static bool IsSeparator(char c) => c is '_' or '-' or ' ';

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex TemplateToken();

    private static bool Taken(string parent, string name)
    {
        try
        {
            string folder = Path.Join(parent, name);
            return File.Exists(folder) || Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string DefaultName(string source, DateTime now, string template)
    {
        string label = "";
        string? camera = null;
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Trim().Trim('"')));
            label = IsDriveRoot(full) ? VolumeInfo.Of(full).Label : Path.GetFileName(full);
            if (template.Contains("{camera}", StringComparison.OrdinalIgnoreCase) && SourceGuards.MemoryCardNote(full) is not null)
                camera = CardCamera.OfDrive(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        label = Sanitize(label);
        string name = ExpandTemplate(template, now, label.Length > 0 ? label : "Card", camera);
        return ValidateName(name) is null ? name : ExpandTemplate(DefaultTemplate, now, label.Length > 0 ? label : "Card", null);
    }

    /// <summary>An error for a folder name that cannot be used, or null.</summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name for the backup folder.";
        if (name != name.Trim() || name.EndsWith('.')) return "The backup folder name cannot start or end with a space, or end with a dot.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            return "The backup folder name cannot contain any of these characters: \\ / : * ? \" < > |";
        if (name is "." or ".." || ReservedNames.Contains(Path.GetFileNameWithoutExtension(name)))
            return $"\"{name}\" cannot be used as a folder name in Windows.";
        return null;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static string Sanitize(string label)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = new(label.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return clean.Trim().TrimEnd('.').Trim();
    }

    private static bool IsDriveRoot(string full) => Path.GetPathRoot(full) is { } root && string.Equals(root.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string SourceName(string source, VolumeInfo? volume) =>
        volume is not null && IsDriveRoot(source) ? SourceGuards.DriveName(volume) : Path.GetFileName(source.TrimEnd('\\')) is { Length: > 0 } n ? n : source;

    /// <summary>
    /// An error for a destination that cannot be used, or null. Outputs the folder the copy goes into, and the log of an
    /// unfinished backup found in it (to offer resuming it).
    /// </summary>
    internal static string? ValidateDestination(string source, uint sourceSerial, string parent, string name, Func<string, string> realPath,
        out string folder, out string? unfinishedJournal)
    {
        folder = "";
        unfinishedJournal = null;
        if (string.IsNullOrWhiteSpace(parent)) return "Choose a destination folder.";
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent.Trim().Trim('"')));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"The destination path is not valid: {e.Message}";
        }
        if (Path.GetPathRoot(full) is not { Length: > 0 } root || !Directory.Exists(root))
            return $"The drive for the destination {full} ({Path.GetPathRoot(full)}) is not connected.";
        if (full[root.Length..].IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0)
            return $"The destination path is not valid (a folder name contains one of : * ? \" < > |): {full}";
        if (File.Exists(full)) return $"The destination {full} is a file, not a folder.";
        if (name.Length == 0) return null; // the name error is reported once
        folder = Path.Join(full, name);
        string realSource = realPath(source), realFolder = realPath(folder);
        if (Planner.IsInside(folder, source) || Planner.IsInside(realFolder, realSource))
            return $"The destination {folder} is inside the folder being backed up. Choose a folder on another drive.";
        if (Planner.IsInside(source, folder) || Planner.IsInside(realSource, realFolder))
            return $"The folder being backed up is inside the destination {folder}. Choose another destination.";
        if (File.Exists(folder)) return $"{folder} is a file. Choose another name for the backup folder.";
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            if (UnfinishedBackupIn(folder) is not { } other)
                return $"{folder} already exists and is not empty. A backup never mixes two cards: choose another name or another destination folder.";
            unfinishedJournal = other.JournalPath;
            string started = other.Header.Created is { Length: >= 16 } at ? at[..16].Replace('T', ' ') : "earlier";
            // The same card is recognized by its drive's serial number (its letter may differ), or else by its path.
            // (and the same folder below the drive's root: the root itself for a card).
            bool sameCard = other.Header.SourceSerial != 0 && sourceSerial != 0
                ? other.Header.SourceSerial == sourceSerial && string.Equals(BelowRoot(other.Header.Source), BelowRoot(source), StringComparison.OrdinalIgnoreCase)
                : SameFolder(other.Header.Source, source, p => p);
            return sameCard
                ? $"{folder} holds an unfinished backup of this card (started {started}). Resume it (above) instead of starting a new one."
                : $"{folder} holds an unfinished backup of another card ({other.Header.SourceLabel}, started {started}). Resume that one (above), or choose another name.";
        }
        return null;
    }

    private static string BelowRoot(string path) => Path.GetRelativePath(Path.GetPathRoot(path) ?? path, path).TrimEnd('\\');

    /// <summary>An unfinished backup whose log is in the folder, or null.</summary>
    private static JobState? UnfinishedBackupIn(string folder)
    {
        try
        {
            foreach (string journal in BackupPaths.FindJournals(folder))
            {
                JobState state = JournalReader.Read(journal);
                if (state.IsBackup && state.PlanComplete && !state.IsEnded) return state;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JournalException)
        {
        }
        return null;
    }

    private static bool SameFolder(string a, string b, Func<string, string> realPath) =>
        JobPaths.SamePath(a, b) || string.Equals(realPath(a).TrimEnd('\\'), realPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Read-only drives, FAT32's 4 GB limit and free space for what is written to each destination (the whole card, or
    /// in a top-up what is new or changed); destinations on the same drive need room for each copy.
    /// </summary>
    private static void CheckSpace(List<BackupTarget> targets, Func<BackupTarget, IReadOnlyCollection<SourceFile>> filesFor, PlanEnvironment env,
        List<PlanMessage> messages)
    {
        foreach (var drive in targets.Where(t => t.Volume is not null).GroupBy(t => (t.Volume!.SerialNumber, t.Volume.Root.ToUpperInvariant())))
        {
            VolumeInfo volume = drive.First().Volume!;
            int copies = drive.Count();
            long cluster = env.ClusterSizeOf(volume.Root);
            // Planner.CheckTarget checks one set of files against the free space; the other copies on the drive take their room first.
            var lists = drive.Select(filesFor).ToList();
            var files = lists.SelectMany(l => l).Distinct().ToList();
            long all = lists.Sum(l => l.Sum(f => f.Size)), others = all - files.Sum(f => f.Size);
            var asIfOne = copies == 1 ? volume : volume with { FreeBytes = Math.Max(0, volume.FreeBytes - others) };
            foreach (PlanMessage m in Planner.CheckTarget(asIfOne, cluster, files, copying: true))
                messages.Add(copies == 1 || !m.Text.StartsWith("Not enough free space", StringComparison.Ordinal)
                    ? m
                    : new PlanMessage(MessageLevel.Error, $"Not enough free space on {volume.DisplayName} for {copies} copies of the card: "
                        + $"{Format.Bytes(all)} needed, {Format.Bytes(volume.FreeBytes)} available."));
        }
    }

    private static void AddScanFindings(BackupScan scan, List<PlanMessage> messages)
    {
        if (scan.Problems.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"{scan.Problems.Count:N0} folder{(scan.Problems.Count == 1 ? "" : "s")} could not be read, so {(scan.Problems.Count == 1 ? "its" : "their")} files are not in the backup: "
                + string.Join("; ", scan.Problems.Take(3)) + (scan.Problems.Count > 3 ? "; ..." : "")));
        if (scan.LeftOut.Count > 0)
            foreach (var group in scan.LeftOut.GroupBy(f => f.Why))
                messages.Add(new PlanMessage(MessageLevel.Warning,
                    $"{group.Count():N0} file{(group.Count() == 1 ? "" : "s")} not copied: {group.Key} (e.g. {group.First().RelativePath})."));
        var links = scan.SkippedFolders.Where(f => !BackupScanner.OsClutter.ContainsKey(Path.GetFileName(f.RelativePath))).ToList();
        if (links.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Warning,
                $"{links.Count:N0} linked folder{(links.Count == 1 ? " is" : "s are")} not copied (links are never followed): {string.Join(", ", links.Take(3).Select(f => f.RelativePath))}."));
        var clutter = scan.SkippedFolders.Where(f => BackupScanner.OsClutter.ContainsKey(Path.GetFileName(f.RelativePath))).ToList();
        if (clutter.Count > 0)
            messages.Add(new PlanMessage(MessageLevel.Info,
                $"Left out: {string.Join(", ", clutter.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Take(5))} (kept by Windows or macOS, not by the camera)."));
    }
}
