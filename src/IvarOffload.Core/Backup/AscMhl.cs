using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Core.Backup;

/// <summary>
/// An ASC MHL history copied from the card can't be continued: it is damaged (unreadable, a manifest missing or changed),
/// uses ignore patterns this app can't apply, or the card no longer matches it (<see cref="CardChanged"/>). Nothing was
/// written: a generation added to it would make the reference tools reject the whole backup's history.
/// </summary>
public sealed class MhlHistoryException(string message, bool cardChanged = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The card's files no longer match the checksums its own history recorded (changed or missing files).</summary>
    public bool CardChanged { get; } = cardChanged;
    /// <summary>The folder of the history that can't be continued, relative to the backup folder ("" for its top folder).</summary>
    public string History { get; init; } = "";
}

/// <summary>A copied file as the ASC MHL manifest records it. Rel uses backslashes, relative to the backup folder.</summary>
public sealed record MhlFile(string Rel, long Size, long LastWriteTime, string Xxh64);

/// <summary>
/// Writes ASC MHL (version 2) histories into a backup destination, so Hedge, Silverstack, the ascmhl tool and other DIT
/// tools can verify the copy independently of IVAR Offload. Each history is an "ascmhl" folder holding numbered generation
/// manifests and a chain file (ascmhl_chain.xml) with the C4 id of each. A history the card already had (its top folder,
/// or a subfolder that was offloaded with MHL before) is continued with a new generation; the top folder gets a new
/// history otherwise, and references the generations written into nested ones. Hashes are xxHash64, with directory
/// content and structure hashes computed the way the ascmhl reference tool does.
/// </summary>
public static class AscMhl
{
    public const string FolderName = "ascmhl";
    public const string ChainFileName = "ascmhl_chain.xml";
    private const string HashFormat = "xxh64";
    private static readonly XNamespace Ns = "urn:ASC:MHL:v2.0";
    private static readonly XNamespace ChainNs = "urn:ASC:MHL:DIRECTORY:v2.0";
    private static readonly string[] DefaultIgnore = [".DS_Store", FolderName, FolderName + "/"];
    /// <summary>Ignore patterns this app applies itself (and IVAR Ingest did); a history with any other can't be continued.</summary>
    private static readonly HashSet<string> KnownIgnore = new(
        [.. DefaultIgnore, .. BackupPaths.MhlIgnorePatterns, .. BackupPaths.LogFolderNames.Select(name => "**/" + name + "/*-backup-*")],
        StringComparer.Ordinal);
    /// <summary>The tool names in the manifests this app writes, and those of IVAR Ingest, this app's earlier name.</summary>
    private static readonly string[] OwnToolNames = ["IVAR Offload", "IVAR Ingest"];
    /// <summary>Hash formats of a card's earlier history that the card can be compared with.</summary>
    private static readonly string[] Formats = ["xxh64", "xxh128", "xxh3", "md5", "sha1", "c4"];
    private static readonly string EmptyXxh64 = SafeXxh64([]);

    /// <summary>Generations in the history at <paramref name="root"/> (its ascmhl folder); 0 when there is none or it can't be read.</summary>
    public static int GenerationsIn(string root)
    {
        string chain = Path.Join(root, FolderName, ChainFileName);
        try
        {
            if (!File.Exists(chain)) return 0;
            // Read without changing anything on the card, not even the file's last-access time.
            return ChainEntries(LoadXml(chain)).Count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The manifest file names this app wrote as the newest generation of every history under <paramref name="root"/>
    /// (relative paths), for the journal. Written deepest history first; the last one is the top folder's.
    /// </summary>
    /// <param name="files">Every copied, verified file of the backup.</param>
    /// <param name="folders">Every folder of the backup with its original times (the dates the folders get).</param>
    /// <param name="orphansTo">
    /// Where a generation manifest this app wrote but no chain lists is moved (keeping its path below the backup folder);
    /// null to delete it (a new backup's own, left by a crash between writing it and adding it to the chain).
    /// </param>
    public static List<string> WriteGenerations(string root, IReadOnlyCollection<MhlFile> files, IReadOnlyDictionary<string, FolderTimes> folders,
        string tool, DateTimeOffset now, string? orphansTo = null)
    {
        // Histories: the top folder, and every folder whose ascmhl folder has a chain file (copied from the card).
        var histories = new List<string> { "" };
        histories.AddRange(files.Where(f => IsChainFile(f.Rel)).Select(f => Parent(Parent(f.Rel))).Where(h => h.Length > 0)
            .Distinct(StringComparer.Ordinal));
        histories = histories.OrderByDescending(h => h.Length == 0 ? 0 : h.Split('\\').Length).ThenBy(h => h, StringComparer.Ordinal).ToList();

        // Every history the card brought along must be intact, and the card must still match it, before anything is
        // written: a history that is left half-continued, or that lists files differently, makes the reference tools
        // reject the whole backup's history.
        var tree = new Tree(files.Where(f => !IsIgnored(f.Rel)), folders.Keys.Where(d => !IsIgnored(d + "\\")))
        {
            Backup = files.Select(f => f.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
        var checkedHistories = new Dictionary<string, History>(StringComparer.Ordinal);
        foreach (string history in histories) checkedHistories[history] = CheckHistory(root, history, tree);
        var written = new Dictionary<string, (string Rel, string C4, NodeHash Hash)>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (string history in histories)
        {
            var nested = histories.Where(h => h.Length > 0 && h != history && IsBelow(h, history)
                                              && !histories.Any(m => m.Length > 0 && m != h && m != history && IsBelow(m, history) && IsBelow(h, m))).ToList();
            (string rel, string c4, NodeHash hash) = WriteGeneration(root, history, checkedHistories[history], tree, folders, nested, written, tool, now, orphansTo);
            written[history] = (rel, c4, hash);
            result.Add(rel);
        }
        return result;
    }

    // ---- One generation --------------------------------------------------------------------------------------

    private static (string Rel, string C4, NodeHash Hash) WriteGeneration(string root, string history, History existing, Tree tree,
        IReadOnlyDictionary<string, FolderTimes> folders, List<string> nested, Dictionary<string, (string Rel, string C4, NodeHash Hash)> written,
        string tool, DateTimeOffset now, string? orphansTo)
    {
        string historyPath = history.Length == 0 ? root : Path.Join(root, history);
        string ascFolder = Path.Join(historyPath, FolderName);
        Directory.CreateDirectory(ascFolder);
        string chainPath = Path.Join(ascFolder, ChainFileName);
        List<ChainEntry> chain = existing.Chain;
        Dictionary<string, string> previous = existing.Xxh64;
        List<string> ignore = [.. existing.Ignore];
        foreach (string pattern in DefaultIgnore.Concat(BackupPaths.MhlIgnorePatterns))
            if (!ignore.Contains(pattern)) ignore.Add(pattern);

        int generation = chain.Count == 0 ? 1 : chain.Max(c => c.Sequence) + 1;
        RemoveOwnOrphans(ascFolder, chain, generation, tree.Backup, orphansTo is null ? null : Path.Join(orphansTo, Path.GetRelativePath(root, ascFolder)));
        string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(historyPath));
        string fileName = $"{generation:0000}_{folderName}_{now.UtcDateTime:yyyy-MM-dd_HHmmss}Z.mhl";

        var hashes = new XElement(Ns + "hashes");
        string hashDate = HashDate(now);
        NodeHash rootHash = HashFolder(history, history, tree, folders, nested, written, hashes, previous, hashDate);

        var manifest = new XElement(Ns + "hashlist", new XAttribute("version", "2.0"),
            new XElement(Ns + "creatorinfo",
                new XElement(Ns + "creationdate", Date(now)),
                new XElement(Ns + "hostname", Environment.MachineName),
                new XElement(Ns + "tool", new XAttribute("version", ToolVersion(tool)), OwnToolNames[0])),
            new XElement(Ns + "processinfo",
                new XElement(Ns + "process", "transfer"),
                new XElement(Ns + "roothash",
                    new XElement(Ns + "content", new XElement(Ns + HashFormat, new XAttribute("hashdate", hashDate), rootHash.Content)),
                    new XElement(Ns + "structure", new XElement(Ns + HashFormat, new XAttribute("hashdate", hashDate), rootHash.Structure))),
                new XElement(Ns + "ignore", ignore.Select(p => new XElement(Ns + "pattern", p)))),
            hashes);
        if (nested.Count > 0)
            manifest.Add(new XElement(Ns + "references", nested.Select(n => new XElement(Ns + "hashlistreference",
                new XElement(Ns + "path", Slashes(Relative(n, history)) + "/" + FolderName + "/" + Path.GetFileName(written[n].Rel)),
                new XElement(Ns + "c4", written[n].C4)))));

        byte[] bytes = Serialize(new XDocument(new XDeclaration("1.0", "UTF-8", null), manifest));
        string manifestPath = Path.Join(ascFolder, fileName);
        WriteNew(manifestPath, bytes);
        string c4 = C4(bytes);

        chain.Add(new ChainEntry(generation, fileName, c4));
        var chainXml = new XElement(ChainNs + "ascmhldirectory",
            chain.Select(c => new XElement(ChainNs + "hashlist", new XAttribute("sequencenr", c.Sequence),
                new XElement(ChainNs + "path", c.Path), new XElement(ChainNs + "c4", c.C4))));
        string chainTemp = chainPath + ".partial";
        File.WriteAllBytes(chainTemp, Serialize(new XDocument(new XDeclaration("1.0", "UTF-8", null), chainXml)));
        FlushFile(chainTemp);
        // A chain copied from a card can be read-only; it is this backup's copy, and gets the new generation.
        if (File.Exists(chainPath) && File.GetAttributes(chainPath).HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(chainPath, File.GetAttributes(chainPath) & ~FileAttributes.ReadOnly);
        File.Move(chainTemp, chainPath, overwrite: true);

        string rel = Path.Join(history, FolderName, fileName);
        return (rel, c4, rootHash);
    }

    private readonly record struct NodeHash(string Content, string Structure);

    /// <summary>
    /// Hashes a folder of this history: files and subfolders, bottom up (post order), adding their entries to the
    /// manifest. A nested history's top folder takes the hashes of the generation written into it, and nothing below it
    /// is listed here.
    /// </summary>
    private static NodeHash HashFolder(string folder, string history, Tree tree, IReadOnlyDictionary<string, FolderTimes> folders,
        List<string> nested, Dictionary<string, (string Rel, string C4, NodeHash Hash)> written, XElement hashes,
        Dictionary<string, string> previous, string hashDate)
    {
        var contents = new List<string>();
        var structures = new List<string>();
        foreach ((string name, bool isFolder) in tree.Children(folder))
        {
            string rel = folder.Length == 0 ? name : folder + "\\" + name;
            string relToHistory = Slashes(Relative(rel, history));
            if (isFolder)
            {
                NodeHash child = nested.Contains(rel, StringComparer.Ordinal)
                    ? written[rel].Hash
                    : HashFolder(rel, history, tree, folders, nested, written, hashes, previous, hashDate);
                contents.Add(child.Content);
                structures.Add(Xxh64Of([.. Encoding.UTF8.GetBytes(name), .. Convert.FromHexString(child.Structure)]));
                var path = new XElement(Ns + "path", relToHistory);
                if (folders.TryGetValue(rel, out FolderTimes t)) path.Add(new XAttribute("lastmodificationdate", FileDate(t.LastWriteTime)));
                hashes.Add(new XElement(Ns + "directoryhash", path,
                    new XElement(Ns + "content", new XElement(Ns + HashFormat, new XAttribute("hashdate", hashDate), child.Content)),
                    new XElement(Ns + "structure", new XElement(Ns + HashFormat, new XAttribute("hashdate", hashDate), child.Structure))));
            }
            else
            {
                MhlFile f = tree.File(rel);
                contents.Add(f.Xxh64);
                structures.Add(Xxh64Of([.. Encoding.UTF8.GetBytes(name), .. Convert.FromHexString(f.Xxh64)]));
                string action = previous.TryGetValue(relToHistory, out string? before) ? before == f.Xxh64 ? "verified" : "failed" : "original";
                hashes.Add(new XElement(Ns + "hash",
                    new XElement(Ns + "path", new XAttribute("size", f.Size), new XAttribute("lastmodificationdate", FileDate(f.LastWriteTime)), relToHistory),
                    new XElement(Ns + HashFormat, new XAttribute("action", action), new XAttribute("hashdate", hashDate), f.Xxh64)));
            }
        }
        return new NodeHash(HashOfHashList(contents), HashOfHashList(structures));
    }

    /// <summary>The files and folders of the backup as a tree, children listed in the order the ascmhl tool uses.</summary>
    private sealed class Tree
    {
        private readonly Dictionary<string, List<(string Name, bool IsFolder)>> _children = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MhlFile> _files = new(StringComparer.OrdinalIgnoreCase);

        public Tree(IEnumerable<MhlFile> files, IEnumerable<string> folders)
        {
            foreach (string d in folders) AddFolder(d);
            foreach (MhlFile f in files)
            {
                _files[f.Rel] = f;
                string parent = Parent(f.Rel);
                AddFolder(parent);
                Get(parent).Add((Path.GetFileName(f.Rel), false));
            }
            // Python sorts names by code point; ordinal comparison of UTF-16 matches it for every name a card holds.
            foreach (List<(string Name, bool IsFolder)> list in _children.Values) list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        private void AddFolder(string rel)
        {
            if (rel.Length == 0 || _children.ContainsKey(rel)) return;
            _children[rel] = [];
            string parent = Parent(rel);
            AddFolder(parent);
            Get(parent).Add((Path.GetFileName(rel), true));
        }

        private List<(string Name, bool IsFolder)> Get(string rel) => _children.TryGetValue(rel, out var list) ? list : _children[rel] = [];

        public IReadOnlyList<(string Name, bool IsFolder)> Children(string rel) => _children.TryGetValue(rel, out var list) ? list : [];

        public MhlFile File(string rel) => _files[rel];

        public bool TryFile(string rel, out MhlFile? file) => _files.TryGetValue(rel, out file);

        /// <summary>Every file of the backup (ignored ones too), relative to its folder.</summary>
        public IReadOnlySet<string> Backup { get; init; } = new HashSet<string>();
    }

    // ---- Existing histories ----------------------------------------------------------------------------------

    private sealed record ChainEntry(int Sequence, string Path, string C4);

    private static List<ChainEntry> ReadChain(string chainPath)
    {
        return File.Exists(chainPath) ? ChainEntries(LoadXml(chainPath)) : [];
    }

    private static List<ChainEntry> ChainEntries(XDocument doc)
    {
        return doc.Root?.Elements().Where(e => e.Name.LocalName == "hashlist").Select(e => new ChainEntry(
            int.TryParse((string?)e.Attribute("sequencenr"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0,
            e.Elements().FirstOrDefault(x => x.Name.LocalName == "path")?.Value ?? "",
            e.Elements().FirstOrDefault(x => x.Name.LocalName == "c4")?.Value ?? "")).ToList() ?? [];
    }

    /// <summary>
    /// An existing history, checked: its chain, the newest xxh64 of every file it lists (for the "verified" action), and
    /// its newest ignore patterns.
    /// </summary>
    private sealed record History(List<ChainEntry> Chain, Dictionary<string, string> Xxh64, List<string> Ignore);

    /// <summary>
    /// Reads the history in <paramref name="history"/> (an empty one when there is none) the way the reference tool
    /// loads it: every manifest its chain lists must be there, unchanged (its C4 id) and readable, and its ignore
    /// patterns must be ones this app applies. Then the card's files must still match what the history recorded, in
    /// whatever hash format it used, and none it lists may be missing. Throws <see cref="MhlHistoryException"/> otherwise.
    /// </summary>
    private static History CheckHistory(string root, string history, Tree tree)
    {
        string where = history.Length == 0 ? "the card's top folder" : history;
        const string NotAdded = "so no ASC MHL manifest was added (the card's files, that history included, are copied and verified as they are)";
        string ascFolder = Path.Join(history.Length == 0 ? root : Path.Join(root, history), FolderName);
        string chainPath = Path.Join(ascFolder, ChainFileName);
        List<ChainEntry> chain;
        // The first (original) hash of every file, in the format it was recorded in, and the newest xxh64.
        var original = new Dictionary<string, (string Format, string Value)>(StringComparer.Ordinal);
        var xxh64 = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string> ignore = [];
        try
        {
            chain = ReadChain(chainPath);
            foreach (ChainEntry entry in chain.OrderBy(c => c.Sequence))
            {
                string path = Path.Join(ascFolder, entry.Path);
                if (!File.Exists(path))
                    throw new MhlHistoryException($"the card's own ASC MHL history in {where} is incomplete (its manifest {entry.Path} is missing), {NotAdded}") { History = history };
                byte[] bytes = ReadBytes(path);
                if (!string.Equals(C4(bytes), entry.C4, StringComparison.Ordinal))
                    throw new MhlHistoryException($"the card's own ASC MHL history in {where} was changed after it was written (its manifest {entry.Path} no longer matches its chain), {NotAdded}") { History = history };
                XDocument doc;
                using (var stream = new MemoryStream(bytes)) doc = XDocument.Load(stream);
                var patterns = doc.Descendants().Where(e => e.Name.LocalName == "ignore").SelectMany(e => e.Elements()).Select(e => e.Value.Trim()).ToList();
                if (patterns.Count > 0) ignore = patterns;
                foreach (XElement hash in doc.Descendants().Where(e => e.Name.LocalName == "hash"))
                {
                    if (hash.Elements().FirstOrDefault(e => e.Name.LocalName == "path")?.Value is not { } rel) continue;
                    foreach (XElement value in hash.Elements().Where(e => Formats.Contains(e.Name.LocalName)))
                    {
                        string format = value.Name.LocalName, v = format == "c4" ? value.Value.Trim() : value.Value.Trim().ToLowerInvariant();
                        original.TryAdd(rel, (format, v));
                        if (format == HashFormat) xxh64[rel] = v;
                    }
                }
            }
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            throw new MhlHistoryException($"the card's own ASC MHL history in {where} can't be read ({e.Message.TrimEnd('.')}), {NotAdded}", inner: e) { History = history };
        }
        if (ignore.FirstOrDefault(p => !KnownIgnore.Contains(p)) is { } custom)
            throw new MhlHistoryException($"the card's own ASC MHL history in {where} leaves out files by its own pattern (\"{custom}\"), which IVAR Offload can't apply yet, {NotAdded}") { History = history };

        var changed = new List<string>();
        var missing = new List<string>();
        foreach ((string relToHistory, (string format, string value)) in original)
        {
            string rel = (history.Length == 0 ? "" : history + "\\") + relToHistory.Replace('/', '\\');
            if (IsIgnored(rel)) continue;
            if (!tree.TryFile(rel, out MhlFile? f)) missing.Add(rel);
            else if ((format == HashFormat ? f!.Xxh64 : HashFileAs(Path.Join(root, rel), format)) != value) changed.Add(rel);
        }
        if (changed.Count + missing.Count > 0)
            throw new MhlHistoryException($"the card no longer matches its own ASC MHL history in {where} (from an earlier offload): "
                + string.Join("; ", new[]
                {
                    changed.Count > 0 ? $"{changed.Count:N0} file{(changed.Count == 1 ? "" : "s")} changed since then (e.g. {changed[0]})" : null,
                    missing.Count > 0 ? $"{missing.Count:N0} missing from the card (e.g. {missing[0]})" : null,
                }.OfType<string>())
                + ". No ASC MHL manifest was added; check those files", cardChanged: true) { History = history };
        return new History(chain, xxh64, ignore);
    }

    /// <summary>A file's hash in an ASC MHL format (lowercase hex; a C4 id for c4), read without touching its last-access time.</summary>
    /// <summary>The xxHash64 of a file (lowercase hex), read without touching its last-access time.</summary>
    public static string Xxh64OfFile(string path) => HashFileAs(path, HashFormat);

    private static string HashFileAs(string path, string format)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle h = IO.SafeFile.OpenRead(path, unbuffered: false);
        using var stream = new FileStream(h, FileAccess.Read, 1024 * 1024);
        switch (format)
        {
            case "md5": return Convert.ToHexStringLower(MD5.HashData(stream));
            case "sha1": return Convert.ToHexStringLower(SHA1.HashData(stream));
            case "c4": return C4FromSha512(SHA512.HashData(stream));
        }
        System.IO.Hashing.NonCryptographicHashAlgorithm hasher = format switch
        {
            "xxh64" => new System.IO.Hashing.XxHash64(),
            "xxh128" => new System.IO.Hashing.XxHash128(),
            _ => new System.IO.Hashing.XxHash3(),
        };
        hasher.Append(stream);
        return Convert.ToHexStringLower(hasher.GetCurrentHash());
    }

    private static byte[] ReadBytes(string path)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle h = IO.SafeFile.OpenRead(path, unbuffered: false);
        using var stream = new FileStream(h, FileAccess.Read);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// A generation manifest this app wrote but never added to the chain (the app stopped in between): it would clash
    /// with the one written now. Only files this app wrote are removed (their creator tool says so).
    /// </summary>
    private static void RemoveOwnOrphans(string ascFolder, List<ChainEntry> chain, int generation, IReadOnlySet<string> backupFiles, string? moveTo)
    {
        foreach (string file in Directory.EnumerateFiles(ascFolder, $"{generation:0000}_*.mhl"))
        {
            if (chain.Any(c => string.Equals(c.Path, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase))) continue;
            if (backupFiles.Any(f => file.EndsWith("\\" + f, StringComparison.OrdinalIgnoreCase))) continue; // copied from the card: never ours to remove
            try
            {
                string? tool = LoadXml(file).Descendants().FirstOrDefault(e => e.Name.LocalName == "tool")?.Value;
                if (tool is null || !OwnToolNames.Contains(tool)) continue;
                if (moveTo is null)
                {
                    File.Delete(file);
                    continue;
                }
                // A top-up: possibly the earlier backup's generation (the card's chain was copied back): kept, not deleted.
                Directory.CreateDirectory(moveTo);
                string to = Path.Join(moveTo, Path.GetFileName(file));
                for (int n = 2; File.Exists(to); n++) to = Path.Join(moveTo, $"{Path.GetFileNameWithoutExtension(file)} ({n}).mhl");
                File.Move(file, to);
            }
            catch (XmlException)
            {
                // Not a complete manifest. Ours are written under a temporary name first, so this one is not ours.
            }
        }
    }

    // ---- Hashes, names and dates -----------------------------------------------------------------------------

    /// <summary>
    /// Reads an XML file without changing anything, not even its last-access time: on the card (its own history) and
    /// on the copies (which must stay exactly like the card).
    /// </summary>
    private static XDocument LoadXml(string path)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle h = IO.SafeFile.OpenRead(path, unbuffered: false);
        using var stream = new FileStream(h, FileAccess.Read);
        return XDocument.Load(stream);
    }

    /// <summary>ascmhl's hash of a hash list: the hex strings sorted, their bytes hashed in that order.</summary>
    private static string HashOfHashList(List<string> hashes)
    {
        if (hashes.Count == 0) return EmptyXxh64;
        var xxh = new System.IO.Hashing.XxHash64();
        foreach (string h in hashes.OrderBy(h => h, StringComparer.Ordinal)) xxh.Append(Convert.FromHexString(h));
        return Convert.ToHexStringLower(xxh.GetCurrentHash());
    }

    private static string Xxh64Of(byte[] data) => SafeXxh64(data);

    private static string SafeXxh64(byte[] data) => Convert.ToHexStringLower(System.IO.Hashing.XxHash64.Hash(data));

    /// <summary>The C4 id of some bytes (SHA-512 in base 58, "c4" and 88 characters), as ascmhl chain files use.</summary>
    public static string C4(byte[] data) => C4FromSha512(SHA512.HashData(data));

    private static string C4FromSha512(byte[] sha512)
    {
        const string Charset = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var value = new BigInteger(sha512, isUnsigned: true, isBigEndian: true);
        var sb = new StringBuilder();
        while (value > 0)
        {
            sb.Insert(0, Charset[(int)(value % 58)]);
            value /= 58;
        }
        return "c4" + sb.ToString().PadLeft(88, '1');
    }

    /// <summary>
    /// What the manifest leaves out, as its ignore patterns say: every "ascmhl" folder, .DS_Store files, and every
    /// _IVAROffload and _IVARIngest log folder (see <see cref="BackupPaths.MhlIgnorePatterns"/>).
    /// </summary>
    private static bool IsIgnored(string rel)
    {
        string[] parts = rel.TrimEnd('\\').Split('\\');
        if (parts.Contains(FolderName, StringComparer.Ordinal) || parts.Any(p => BackupPaths.LogFolderNames.Contains(p, StringComparer.Ordinal))) return true;
        return !rel.EndsWith('\\') && parts[^1] == ".DS_Store";
    }

    /// <summary>An ASC MHL chain file (ascmhl\ascmhl_chain.xml in any folder), relative to the backup folder.</summary>
    public static bool IsChainFile(string rel) =>
        Path.GetFileName(rel) == ChainFileName && Path.GetFileName(Parent(rel)) == FolderName;

    private static string Parent(string rel) => Path.GetDirectoryName(rel) ?? "";

    private static bool IsBelow(string path, string folder) => folder.Length == 0 ? path.Length > 0 : path.StartsWith(folder + "\\", StringComparison.Ordinal);

    private static string Relative(string rel, string history) => history.Length == 0 ? rel : rel[(history.Length + 1)..];

    private static string Slashes(string rel) => rel.Replace('\\', '/');

    private static string ToolVersion(string tool) => tool.Split(' ').LastOrDefault() is { Length: > 0 } v && char.IsDigit(v[0]) ? v : "1.0";

    private static string Date(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string HashDate(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);

    private static string FileDate(long fileTimeUtc) => Date(new DateTimeOffset(DateTime.FromFileTimeUtc(fileTimeUtc)).ToLocalTime());

    private static byte[] Serialize(XDocument doc)
    {
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "  ", NewLineChars = "\n" };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings)) doc.Save(writer);
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <summary>Writes a new file under a temporary name, flushes it, then gives it its name (never replacing one).</summary>
    private static void WriteNew(string path, byte[] bytes)
    {
        string temp = path + ".partial";
        File.WriteAllBytes(temp, bytes);
        FlushFile(temp);
        File.Move(temp, path, overwrite: false);
    }

    private static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Flush(flushToDisk: true);
    }
}
