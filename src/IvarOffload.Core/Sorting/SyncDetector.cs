using IvarOffload.Core.IO;
using Microsoft.Win32.SafeHandles;
using static IvarOffload.Core.IO.Native;

namespace IvarOffload.Core.Sorting;

/// <summary>Finds out whether a folder is synchronized by a sync tool, because moving files out of it is then seen as a delete.</summary>
public static class SyncDetector
{
    /// <summary>The sync tool that owns the folder ("OneDrive", "Resilio Sync (shared folder ...)"), or null. Never throws.</summary>
    public static string? Describe(string folder)
    {
        try
        {
            string? start = VolumeInfo.NearestExistingFolder(Path.GetFullPath(folder));
            if (start is null) return null;
            return CloudFilesProvider(start) ?? GoogleDriveVolume(start) ?? FromMarkers(start) ?? OneDriveFromEnvironment(folder);
        }
        catch (Exception)
        {
            // Sync detection only adds a warning; it must never stop a preview.
            return null;
        }
    }

    /// <summary>
    /// Sync providers built on the Windows Cloud Files API: OneDrive (every account, SharePoint and Teams libraries),
    /// iCloud Drive and iCloud Photos, Box, Dropbox, Nextcloud, Synology Drive on-demand and others.
    /// </summary>
    private static unsafe string? CloudFilesProvider(string folder)
    {
        try
        {
            const int size = 4096;
            byte* buffer = stackalloc byte[size];
            new Span<byte>(buffer, size).Clear();
            if (CfGetSyncRootInfoByPath(folder, CF_SYNC_ROOT_INFO_PROVIDER, buffer, size, out _) < 0) return null;
            var name = new ReadOnlySpan<char>(buffer + CF_PROVIDER_NAME_OFFSET, CF_MAX_PROVIDER_NAME_LENGTH + 1);
            int end = name.IndexOf('\0');
            string provider = (end < 0 ? name : name[..end]).ToString().Trim();
            return provider.Length > 0 ? provider : "a cloud sync app";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null; // Windows without the Cloud Files API
        }
    }

    /// <summary>Google Drive for desktop streams "My Drive" as a virtual drive with this label.</summary>
    private static string? GoogleDriveVolume(string folder)
    {
        try
        {
            VolumeInfo volume = VolumeInfo.Of(folder);
            return volume.Label.Equals("Google Drive", StringComparison.OrdinalIgnoreCase) ? $"Google Drive (drive {volume.DisplayName})" : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FromMarkers(string start)
    {
        for (DirectoryInfo? d = new(start); d is not null; d = d.Parent)
        {
            try
            {
                if (File.Exists(Path.Join(d.FullName, ".sync", "ID")))
                    return $"Resilio Sync (shared folder \"{d.FullName}\")";
                if (Directory.Exists(Path.Join(d.FullName, ".stfolder")))
                    return $"Syncthing (folder \"{d.FullName}\")";
                if (File.Exists(Path.Join(d.FullName, ".dropbox")) || Directory.Exists(Path.Join(d.FullName, ".dropbox.cache")))
                    return $"Dropbox (folder \"{d.FullName}\")";
                // Google Drive for desktop in mirror mode (and the older Backup and Sync).
                if (Directory.Exists(Path.Join(d.FullName, ".tmp.drivedownload")) || Directory.Exists(Path.Join(d.FullName, ".tmp.driveupload"))
                    || IsGoogleDriveIconFolder(d.FullName))
                    return $"Google Drive (folder \"{d.FullName}\")";
                if (Directory.Exists(Path.Join(d.FullName, ".SynologyWorkingDirectory")))
                    return $"Synology Drive (folder \"{d.FullName}\")";
                if (Directory.EnumerateFiles(d.FullName, ".sync_*.db").Any() || Directory.EnumerateFiles(d.FullName, "._sync_*.db").Any())
                    return $"Nextcloud / ownCloud (folder \"{d.FullName}\")";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A parent we cannot read is not a reason to fail the scan.
            }
        }
        return null;
    }

    /// <summary>Google Drive gives its mirrored folder a custom icon through desktop.ini.</summary>
    private static bool IsGoogleDriveIconFolder(string folder)
    {
        var ini = new FileInfo(Path.Join(folder, "desktop.ini"));
        if (!ini.Exists || ini.Length > 16 * 1024) return false;
        // Read through a handle that leaves the file's last-access time alone: the preview changes nothing.
        using SafeFileHandle h = SafeFile.OpenRead(ini.FullName, unbuffered: false);
        using var reader = new StreamReader(new FileStream(h, FileAccess.Read));
        return reader.ReadToEnd().Contains("googledrive", StringComparison.OrdinalIgnoreCase);
    }

    private static string? OneDriveFromEnvironment(string folder)
    {
        foreach (string variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            string? oneDrive = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(oneDrive) && IsInside(folder, oneDrive))
                return $"OneDrive (\"{oneDrive}\")";
        }
        return null;
    }

    private static bool IsInside(string path, string folder)
    {
        string p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        string f = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }
}
