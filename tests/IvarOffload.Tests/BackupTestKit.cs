using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using IvarOffload.Core.Backup;
using IvarOffload.Core.Jobs;

namespace IvarOffload.Tests;

/// <summary>A temporary card folder and up to three destination folders, with helpers to fill and check them.</summary>
public sealed class BackupTree : IDisposable
{
    private static readonly DateTime Created = new(2024, 5, 6, 7, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Accessed = new(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private int _counter;

    public string Root { get; }
    public string Card { get; }
    public string[] Parents { get; }
    public const string Name = "2024-05-06_CARD";

    public BackupTree()
    {
        Root = Path.Join(Path.GetTempPath(), "ivar-backup-" + Guid.NewGuid().ToString("N")[..8]);
        Card = Path.Join(Root, "CARD");
        Parents = [Path.Join(Root, "D1"), Path.Join(Root, "D2"), Path.Join(Root, "D3")];
        Directory.CreateDirectory(Card);
        foreach (string p in Parents) Directory.CreateDirectory(p);
        Environment.SetEnvironmentVariable("IVAROFFLOAD_DATA", Path.Join(Root, "appdata"));
    }

    /// <summary>Where the copy goes on destination k (0-based).</summary>
    public string Dest(int k) => Path.Join(Parents[k], Name);

    /// <summary>Creates a card file with unique random content and distinctive old timestamps.</summary>
    public string Add(string rel, int size = 4096, FileAttributes attributes = 0)
    {
        string path = Path.Join(Card, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] data = new byte[size];
        new Random(++_counter).NextBytes(data);
        File.WriteAllBytes(path, data);
        File.SetCreationTimeUtc(path, Created.AddMinutes(_counter));
        File.SetLastWriteTimeUtc(path, Created.AddMinutes(_counter).AddSeconds(17).AddTicks(7654321));
        File.SetLastAccessTimeUtc(path, Accessed.AddMinutes(_counter));
        if (attributes != 0) File.SetAttributes(path, attributes);
        return path;
    }

    /// <summary>An empty card folder with distinctive dates.</summary>
    public string AddFolder(string rel)
    {
        string path = Path.Join(Card, rel);
        Directory.CreateDirectory(path);
        Directory.SetCreationTimeUtc(path, Created.AddDays(-3));
        Directory.SetLastWriteTimeUtc(path, Created.AddDays(-2));
        return path;
    }

    /// <summary>A small card: photos, a clip, a sidecar, a hidden read-only file, an empty folder and a multi-chunk file.</summary>
    public void TypicalCard(bool large = false)
    {
        Add(@"DCIM\100MSDCF\DSC00001.JPG", 30_000);
        Add(@"DCIM\100MSDCF\DSC00001.ARW", 70_000);
        Add(@"PRIVATE\M4ROOT\CLIP\C0001.MP4", large ? 9 * 1024 * 1024 + 123 : 200_000);
        Add(@"PRIVATE\M4ROOT\CLIP\C0001M01.XML", 1500);
        Add(@"PRIVATE\M4ROOT\MEDIAPRO.XML", 900, FileAttributes.Hidden | FileAttributes.ReadOnly);
        Add("empty.bin", 0);
        Add(@"MISC\ümlaut ☃.txt", 100);
        AddFolder(@"PRIVATE\M4ROOT\SUB");
        AddFolder("EMPTY");
    }

    public BackupPlan Plan(int destinations = 2, bool reread = true) =>
        BackupPlanner.Build(BackupScanner.Scan(Card), Parents.Take(destinations).ToList(), Name, reread);

    public BackupResult Run(BackupPlan plan, IFaultInjector? faults = null, Func<string, long?>? freeBytes = null)
    {
        using BackupRunner runner = BackupRunner.Start(plan, Quick(faults, freeBytes));
        return runner.Run();
    }

    public static BackupResult Resume(string journal, IFaultInjector? faults = null, Func<string, long?>? freeBytes = null)
    {
        using BackupRunner runner = BackupRunner.Open(journal, Quick(faults, freeBytes));
        return runner.Run();
    }

    public static BackupResult Close(string journal)
    {
        using BackupRunner runner = BackupRunner.Open(journal, Quick(null, null));
        return runner.Close();
    }

    public static BackupOptions Quick(IFaultInjector? faults, Func<string, long?>? freeBytes) =>
        new() { Faults = faults, RetryDelaysMs = [10, 10], FreeBytes = freeBytes };

    public string Journal(int k) => BackupPaths.FindJournals(Dest(k)).Single();

    /// <summary>Fingerprints (content, size, three times, attributes) of every card file, keyed by relative path.</summary>
    public Dictionary<string, (string, long, DateTime, DateTime, DateTime, FileAttributes)> CardFingerprints() => Fingerprints(Card);

    public static Dictionary<string, (string, long, DateTime, DateTime, DateTime, FileAttributes)> Fingerprints(string root) =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Select(f => Path.GetRelativePath(root, f))
            .Where(r => !BackupPaths.LogFolderNames.Any(name => r.StartsWith(name + "\\", StringComparison.OrdinalIgnoreCase)) && !r.StartsWith("ascmhl\\", StringComparison.Ordinal))
            .ToDictionary(r => r, r => ((string, long, DateTime, DateTime, DateTime, FileAttributes))TestTree.Fingerprint(Path.Join(root, r)), StringComparer.OrdinalIgnoreCase);

    /// <summary>Metadata of every file and folder under the card, read without opening any file's content.</summary>
    public Dictionary<string, (long, DateTime, DateTime, DateTime, FileAttributes)> CardMetadata() =>
        new DirectoryInfo(Card).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .ToDictionary(i => Path.GetRelativePath(Card, i.FullName),
                i => (i is FileInfo f ? f.Length : -1, i.CreationTimeUtc, i.LastWriteTimeUtc, i.LastAccessTimeUtc, i.Attributes));

    /// <summary>Asserts that destination k holds an exact copy of the card: every file bit for bit with its times and attributes, every folder with its dates.</summary>
    public void AssertExactCopy(int k, Dictionary<string, (string, long, DateTime, DateTime, DateTime, FileAttributes)> card)
    {
        var copy = Fingerprints(Dest(k));
        Assert.Equal(card.Keys.Order(StringComparer.OrdinalIgnoreCase), copy.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach ((string rel, var expected) in card) Assert.Equal(expected, copy[rel]);
        foreach (string dir in Directory.EnumerateDirectories(Card, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(Card, dir);
            if (BackupScanner.OsClutter.ContainsKey(Path.GetFileName(rel))) continue;
            string there = Path.Join(Dest(k), rel);
            Assert.True(Directory.Exists(there), $"folder missing on destination {k}: {rel}");
            Assert.Equal(Directory.GetLastWriteTimeUtc(dir), Directory.GetLastWriteTimeUtc(there));
            Assert.Equal(Directory.GetCreationTimeUtc(dir), Directory.GetCreationTimeUtc(there));
        }
        Assert.Empty(Directory.EnumerateFiles(Dest(k), "*" + JobPaths.TempExtension, SearchOption.AllDirectories));
    }

    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private const FileSystemRights NoWriting = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;

    /// <summary>Makes the card as unwritable as a write-protected card is for this user (or writable again).</summary>
    public void WriteProtect(bool on)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(Card, "*", SearchOption.AllDirectories).Prepend(Card))
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            FileSystemSecurity security = info is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)info).GetAccessControl();
            var rule = new FileSystemAccessRule(Me, NoWriting, AccessControlType.Deny);
            if (on) security.AddAccessRule(rule);
            else security.RemoveAccessRule(rule);
            if (info is DirectoryInfo dir) dir.SetAccessControl((DirectorySecurity)security);
            else ((FileInfo)info).SetAccessControl((FileSecurity)security);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Card)) WriteProtect(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        try
        {
            // Junctions first, so nothing is deleted through them.
            foreach (string d in Directory.EnumerateDirectories(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).ToList())
                if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d);
            foreach (string f in Directory.EnumerateFiles(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Runs the ASC MHL reference tool (pip install ascmhl) against a folder.</summary>
public static class AscMhlTool
{
    /// <summary>ascmhl-debug.exe (for verify) or ascmhl.exe, from PATH or the user's Python scripts folders.</summary>
    public static string Find(string exe)
    {
        var candidates = new List<string>();
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) candidates.Add(Path.Join(dir, exe));
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Programs" })
        {
            string python = Path.Join(root, "Python");
            if (Directory.Exists(python)) candidates.AddRange(Directory.EnumerateDirectories(python).Select(d => Path.Join(d, "Scripts", exe)));
        }
        return candidates.FirstOrDefault(File.Exists)
               ?? throw new InvalidOperationException($"{exe} not found. Install the ASC MHL reference tool: python -m pip install --user ascmhl "
                                                      + "(or leave these tests out: dotnet test --filter Category!=ascmhl).");
    }

    public static (int Exit, string Output) Run(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(Find(exe)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args) info.ArgumentList.Add(a);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(info)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd() + err.Result;
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}
