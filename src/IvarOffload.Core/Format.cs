using System.Globalization;

namespace IvarOffload.Core;

public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} bytes" : value.ToString(value < 10 ? "0.00" : value < 100 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds:00}s" : $"{Math.Max(0, t.Seconds)}s";

    public static string Rate(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

    /// <summary>"1 file", "3 files"; <paramref name="many"/> for irregular plurals ("libraries").</summary>
    public static string Count(int count, string what, string? many = null) => count == 1 ? $"1 {what}" : $"{count:N0} {many ?? what + "s"}";

    /// <summary>When a job was created, as its log recorded it: "26 Sep 15:42" (with the year when it is not this year).</summary>
    public static string JobDate(string? created) =>
        DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset t)
            ? t.LocalDateTime.ToString(t.LocalDateTime.Year == DateTime.Now.Year ? "d MMM HH:mm" : "d MMM yyyy HH:mm", CultureInfo.CurrentCulture)
            : created ?? "";
}
