using System.Security.Cryptography;
using IvarOffload.Core.Jobs;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Tests;

/// <summary>A temporary source/target pair with helpers to create files with known content and timestamps.</summary>
public sealed class TestTree : IDisposable
{
    private static readonly DateTime Created = new(2023, 3, 19, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Accessed = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private int _counter;

    public string Root { get; }
    public string Source { get; }
    public string Target { get; }

    public TestTree()
    {
        Root = Path.Join(Path.GetTempPath(), "offload-test-" + Guid.NewGuid().ToString("N")[..8]);
        Source = Path.Join(Root, "INGEST");
        Target = Path.Join(Root, "INGEST-Video");
        Directory.CreateDirectory(Source);
        Environment.SetEnvironmentVariable("IVAROFFLOAD_DATA", Path.Join(Root, "appdata"));
    }

    /// <summary>Creates a file with unique random content and distinctive old timestamps.</summary>
    public string Add(string rel, int size = 4096, FileAttributes attributes = 0)
    {
        string path = Path.Join(Source, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] data = new byte[size];
        new Random(++_counter).NextBytes(data);
        File.WriteAllBytes(path, data);
        File.SetCreationTimeUtc(path, Created.AddMinutes(_counter));
        File.SetLastWriteTimeUtc(path, Created.AddMinutes(_counter).AddSeconds(13).AddTicks(1234567));
        File.SetLastAccessTimeUtc(path, Accessed.AddMinutes(_counter));
        if (attributes != 0) File.SetAttributes(path, attributes);
        return path;
    }

    public MovePlan Plan(MoveMode mode = MoveMode.Videos, bool verify = true, string? target = null) =>
        Planner.Build(Scanner.Scan(Source), target ?? Target, mode, verify);

    /// <summary>Runs a plan to completion and returns the result.</summary>
    public RunResult Run(MovePlan plan, IFaultInjector? faults = null)
    {
        using JobRunner runner = JobRunner.Start(plan, new RunOptions { Faults = faults });
        return runner.Run();
    }

    public static RunResult Resume(string journal)
    {
        using JobRunner runner = JobRunner.Open(journal);
        return runner.Run();
    }

    public string Journal => JobPaths.FindJournals(Target).Single();

    public static string Sha(string path)
    {
        using FileStream s = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    /// <summary>Fingerprint that must survive a move unchanged: content, size, all three times, attributes.</summary>
    public static (string Sha, long Size, DateTime C, DateTime W, DateTime A, FileAttributes Attr) Fingerprint(string path)
    {
        var info = new FileInfo(path);
        (long size, DateTime c, DateTime w, DateTime a, FileAttributes attr) =
            (info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, info.LastAccessTimeUtc, info.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
        string sha = Sha(path);
        // Reading updated the last-access time; put it back so the fingerprint can be taken again later.
        if (attr.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, info.Attributes & ~FileAttributes.ReadOnly);
        File.SetLastAccessTimeUtc(path, a);
        if (attr.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, info.Attributes);
        return (sha, size, c, w, a, attr);
    }

    public void Dispose()
    {
        try
        {
            // Remove junctions first so nothing is deleted through them, then clear read-only flags and delete.
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

/// <summary>Simulates a crash at a named point by throwing an exception the runner does not handle.</summary>
public sealed class CrashAt(string point, int occurrence = 1) : IFaultInjector
{
    private int _hits;

    public void Hit(string name, int itemIndex)
    {
        if (name == point && ++_hits == occurrence) throw new SimulatedCrash(name);
    }
}

public sealed class SimulatedCrash(string point) : Exception($"simulated crash at {point}");
