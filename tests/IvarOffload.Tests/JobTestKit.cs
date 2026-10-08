using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Tests;

/// <summary>Runs an action at a named point of the move sequence: at the listed occurrences, or every time when none are given.</summary>
public sealed class ActionAt(string point, Action<int> action, params int[] occurrences) : IFaultInjector
{
    private int _hits;

    public void Hit(string name, int itemIndex)
    {
        if (name != point) return;
        _hits++;
        if (occurrences.Length == 0 || occurrences.Contains(_hits)) action(itemIndex);
    }
}

/// <summary>Throws the given exception at a named point (the n-th time it is reached).</summary>
public sealed class ThrowAt(string point, Func<Exception> error, int occurrence = 1) : IFaultInjector
{
    private int _hits;

    public void Hit(string name, int itemIndex)
    {
        if (name == point && ++_hits == occurrence) throw error();
    }
}

/// <summary>Helpers for the engine tests (plans, runs, disk manipulation).</summary>
public static class JobTestKit
{
    /// <summary>Retries after 10 ms instead of up to 4 s, so tests of denied or locked files stay fast.</summary>
    public static RunOptions Quick(IFaultInjector? faults = null) => new() { Faults = faults, RetryDelaysMs = [10, 10] };

    /// <summary>A plan that uses copy + verify + delete even though source and target share a drive.</summary>
    public static MovePlan AsCopy(MovePlan p) => With(p, TransferMethod.Copy, p.ToMove);

    /// <summary>The same plan with a different list (or order) of files to move.</summary>
    public static MovePlan WithToMove(MovePlan p, IReadOnlyList<SourceFile> toMove) => With(p, p.Method, toMove);

    private static MovePlan With(MovePlan p, TransferMethod method, IReadOnlyList<SourceFile> toMove) => new()
    {
        Scan = p.Scan,
        SourceRoot = p.SourceRoot,
        TargetRoot = p.TargetRoot,
        Mode = p.Mode,
        Method = method,
        VerifyChecksums = p.VerifyChecksums || method == TransferMethod.Copy,
        SourceVolume = p.SourceVolume,
        TargetVolume = p.TargetVolume,
        ToMove = toMove,
        Staying = p.Staying,
        Conflicts = p.Conflicts,
        Messages = p.Messages,
        ByType = p.ByType,
        ByFolder = p.ByFolder,
    };

    public static RunResult Run(MovePlan plan, RunOptions options)
    {
        using JobRunner runner = JobRunner.Start(plan, options);
        return runner.Run();
    }

    public static RunResult Resume(string journal, RunOptions? options = null)
    {
        using JobRunner runner = JobRunner.Open(journal, options);
        return runner.Run();
    }

    /// <summary>Changes one byte and puts every timestamp and attribute back, so only the content differs.</summary>
    public static void FlipByte(string path, long offset = 1000)
    {
        var info = new FileInfo(path);
        (DateTime c, DateTime w, DateTime a, FileAttributes attributes) = (info.CreationTimeUtc, info.LastWriteTimeUtc, info.LastAccessTimeUtc, info.Attributes);
        File.SetAttributes(path, FileAttributes.Normal);
        using (var s = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            s.Position = offset;
            int b = s.ReadByte();
            s.Position = offset;
            s.WriteByte((byte)(b ^ 0xFF));
        }
        File.SetCreationTimeUtc(path, c);
        File.SetLastWriteTimeUtc(path, w);
        File.SetLastAccessTimeUtc(path, a);
        File.SetAttributes(path, attributes);
    }

    /// <summary>Renames a folder, retrying while an indexer or antivirus briefly holds something inside it.</summary>
    public static void MoveFolder(string from, string to)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Copies a folder with everything in it (log folders too), the way a backup or Explorer's copy does: files keep their dates.</summary>
    public static void CopyFolder(string from, string to)
    {
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories).Prepend(from))
            Directory.CreateDirectory(Path.Join(to, Path.GetRelativePath(from, dir)));
        foreach (string file in Directory.EnumerateFiles(from, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            File.Copy(file, Path.Join(to, Path.GetRelativePath(from, file)));
    }

    /// <summary>Fingerprints of every file under a folder, except the tool's own log folders (_IVAROffload, _IngestSorter).</summary>
    public static Dictionary<string, (string, long, DateTime, DateTime, DateTime, FileAttributes)> Fingerprints(string root) =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Where(f => !InLogFolder(Path.GetRelativePath(root, f)))
            .ToDictionary(f => Path.GetRelativePath(root, f), f => (((string, long, DateTime, DateTime, DateTime, FileAttributes))TestTree.Fingerprint(f)));

    /// <summary>Media files (anything outside the log folders) under a folder.</summary>
    public static List<string> Files(string root) =>
        !Directory.Exists(root) ? [] : Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Select(f => Path.GetRelativePath(root, f))
            .Where(r => !InLogFolder(r)).ToList();

    /// <summary>A relative path inside one of the tool's log folders (current or legacy).</summary>
    public static bool InLogFolder(string rel) => rel.Contains('\\') && JobPaths.IsLogFolderName(rel[..rel.IndexOf('\\')]);

    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    /// <summary>Denies (or allows again) deleting a file, and deleting files from its folder, for the current user.</summary>
    public static void DenyDelete(string file, bool deny)
    {
        var fileInfo = new FileInfo(file);
        FileSecurity fileSecurity = fileInfo.GetAccessControl();
        var fileRule = new FileSystemAccessRule(Me, FileSystemRights.Delete, AccessControlType.Deny);
        if (deny) fileSecurity.AddAccessRule(fileRule);
        else fileSecurity.RemoveAccessRule(fileRule);
        fileInfo.SetAccessControl(fileSecurity);

        var folderInfo = new DirectoryInfo(Path.GetDirectoryName(file)!);
        DirectorySecurity folderSecurity = folderInfo.GetAccessControl();
        var folderRule = new FileSystemAccessRule(Me, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny);
        if (deny) folderSecurity.AddAccessRule(folderRule);
        else folderSecurity.RemoveAccessRule(folderRule);
        folderInfo.SetAccessControl(folderSecurity);
    }

    public static int Lines(string journal, string type) => File.ReadAllLines(journal).Count(l => l.Contains($"\"t\":\"{type}\""));

    /// <summary>Changes the header of a job log (e.g. to pretend the job was made with other drive letters or another drive).</summary>
    public static void RewriteHeader(string journal, Action<JsonNode> change)
    {
        string[] lines = File.ReadAllLines(journal);
        JsonNode header = JsonNode.Parse(lines[0])!;
        change(header);
        lines[0] = header.ToJsonString();
        File.WriteAllText(journal, string.Join("\n", lines) + "\n");
    }

    /// <summary>The same path under a drive letter that does not exist on this PC ("C:\x" becomes e.g. "Q:\x").</summary>
    public static string OnMissingDrive(string path)
    {
        char letter = Enumerable.Range('D', 23).Select(c => (char)c).Reverse().First(c => !Directory.Exists($"{c}:\\"));
        return letter + path[1..];
    }

    /// <summary>Makes a directory junction (the way a folder on another drive can appear inside this one).</summary>
    public static void Junction(string link, string target)
    {
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        Assert.True(Directory.Exists(link), "junction was not created");
    }
}
