using System.Diagnostics;
using IvarOffload.Core.IO;

namespace IvarOffload.Core.Jobs;

/// <summary>Lets the UI pause a running job between chunks of work.</summary>
public sealed class PauseGate
{
    private readonly ManualResetEventSlim _open = new(initialState: true);

    public bool IsPaused => !_open.IsSet;
    public void Pause() => _open.Reset();
    public void Resume() => _open.Set();

    /// <summary>Waits while paused. Returns true when it actually had to wait (the job was paused).</summary>
    public bool Wait(CancellationToken ct)
    {
        if (_open.IsSet) return false;
        _open.Wait(ct);
        return true;
    }
}

/// <summary>Keeps Windows from going to sleep while a job runs (the display may still turn off).</summary>
public sealed class KeepAwake : IDisposable
{
    public KeepAwake() => Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);
    public void Dispose() => Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
}

/// <summary>Test hook: named points in the move sequence where a crash can be simulated.</summary>
public interface IFaultInjector
{
    void Hit(string point, int itemIndex);
}

/// <summary>The test hooks enabled by environment variables (test use only; nothing is enabled in normal use).</summary>
public static class TestFaults
{
    public static IFaultInjector? FromEnvironment()
    {
        IFaultInjector?[] all = [ProcessKillFaultInjector.FromEnvironment(), HoldFaultInjector.FromEnvironment()];
        IFaultInjector[] active = all.OfType<IFaultInjector>().ToArray();
        return active.Length switch
        {
            0 => null,
            1 => active[0],
            _ => new Composite(active),
        };
    }

    private sealed class Composite(IFaultInjector[] injectors) : IFaultInjector
    {
        public void Hit(string point, int itemIndex)
        {
            foreach (IFaultInjector injector in injectors) injector.Hit(point, itemIndex);
        }
    }

    internal static (string Point, int Occurrence)? Parse(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) return null;
        string[] parts = value.Split(':');
        return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 1);
    }
}

/// <summary>
/// Kills the process at a chosen point, to prove that a job survives a hard stop.
/// Enabled only by the environment variable IVAROFFLOAD_TEST_CRASH_AT = "point" or "point:N" (N-th time the point is reached).
/// </summary>
public sealed class ProcessKillFaultInjector(string point, int occurrence) : IFaultInjector
{
    public const string Variable = "IVAROFFLOAD_TEST_CRASH_AT";
    private int _hits;

    public static IFaultInjector? FromEnvironment() =>
        TestFaults.Parse(Variable) is { } p ? new ProcessKillFaultInjector(p.Point, p.Occurrence) : null;

    public void Hit(string name, int itemIndex)
    {
        if (name != point || ++_hits != occurrence) return;
        Console.Error.WriteLine($"[test] simulated crash at '{name}' (item {itemIndex})");
        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    }
}

/// <summary>
/// Holds the job at a chosen point so a test script can change the disk underneath it (for example unplug or rename
/// the source) and then let it continue. Enabled only by IVAROFFLOAD_TEST_HOLD_AT = "point" or "point:N".
/// When the point is reached the file <see cref="FlagFile"/> is created; the job continues once the script deletes it.
/// </summary>
public sealed class HoldFaultInjector(string point, int occurrence) : IFaultInjector
{
    public const string Variable = "IVAROFFLOAD_TEST_HOLD_AT";
    private int _hits;

    public static string FlagFile => Path.Join(RecentJobs.AppDataFolder, "test-hold.flag");

    public static IFaultInjector? FromEnvironment() =>
        TestFaults.Parse(Variable) is { } p ? new HoldFaultInjector(p.Point, p.Occurrence) : null;

    public void Hit(string name, int itemIndex)
    {
        if (name != point || ++_hits != occurrence) return;
        Directory.CreateDirectory(RecentJobs.AppDataFolder);
        File.WriteAllText(FlagFile, $"{name} {itemIndex}");
        Console.Error.WriteLine($"[test] holding at '{name}' (item {itemIndex}) until {FlagFile} is deleted");
        var clock = Stopwatch.StartNew();
        while (File.Exists(FlagFile) && clock.Elapsed < TimeSpan.FromMinutes(10)) Thread.Sleep(100);
    }
}
