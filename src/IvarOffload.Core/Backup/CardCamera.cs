using System.Text.RegularExpressions;
using IvarOffload.Core.Sorting;

namespace IvarOffload.Core.Backup;

/// <summary>
/// The camera make a card comes from ("Sony", "Canon", "RED", ...), read from the folders cameras write at its root: for
/// the card list and the {camera} part of a backup's folder name. Only a hint: null when the folders don't say.
/// </summary>
public static partial class CardCamera
{
    /// <summary>
    /// The make of the camera that wrote the card at <paramref name="volumeRoot"/>, or null. Folders only one make uses
    /// (Sony's M4ROOT, a RED .RDM, ...) count first, then the name of the camera folder in DCIM ("100MSDCF" is Sony,
    /// "100CANON" Canon, ...).
    /// </summary>
    public static string? Of(string volumeRoot, Func<string, IReadOnlyCollection<string>> subfolders, Func<string, IReadOnlyCollection<string>> files)
    {
        IReadOnlyList<string> signs = SourceGuards.CardSignsAtRoot(volumeRoot, subfolders, files);
        string? fromDcim = null;
        foreach (string sign in signs)
        {
            string path = Path.Join(volumeRoot, sign);
            string? make = sign.ToUpperInvariant() switch
            {
                "PRIVATE" => subfolders(path).Select(PrivateMake).FirstOrDefault(m => m is not null),
                "XDROOT" or "BPAV" or "MP_ROOT" => "Sony",
                "CLIPS001" => "Canon",
                "CONTENTS" => CardStructures.Describe(sign, subfolders(path), _ => false) switch
                {
                    "Panasonic P2 card structure" => "Panasonic",
                    "Canon XF card structure" => "Canon",
                    _ => null,
                },
                "DCIM" => null,
                _ when sign.EndsWith(".RDM", StringComparison.OrdinalIgnoreCase) => "RED",
                _ when Path.GetExtension(sign).Equals(".braw", StringComparison.OrdinalIgnoreCase) => "Blackmagic",
                _ when ArriReel().IsMatch(sign) => "ARRI",
                _ => null,
            };
            if (make is not null) return make;
            if (sign.Equals("DCIM", StringComparison.OrdinalIgnoreCase))
                fromDcim ??= subfolders(path).Select(DcimMake).FirstOrDefault(m => m is not null);
        }
        return fromDcim;
    }

    /// <summary>The make of the camera card that holds <paramref name="folder"/> (looked at from its drive's root), or null. Never throws.</summary>
    public static string? OfDrive(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder.Trim().Trim('"')));
            return string.IsNullOrEmpty(root) ? null : Of(root, DriveFacts.SubfolderNames, DriveFacts.FileNames);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? PrivateMake(string folder) => folder.ToUpperInvariant() switch
    {
        "M4ROOT" or "XDROOT" or "SONY" => "Sony",
        "PANA_GRP" => "Panasonic",
        _ => null, // AVCHD: several makes write it
    };

    /// <summary>
    /// The make from a DCIM camera folder: three digits and five characters the maker chooses ("100MSDCF", "101CANON",
    /// "100_PANA"), or a name of its own ("DJI_001", Insta360's "Camera01").
    /// </summary>
    internal static string? DcimMake(string folder)
    {
        if (folder.StartsWith("DJI", StringComparison.OrdinalIgnoreCase)) return "DJI";
        if (folder.StartsWith("Camera0", StringComparison.OrdinalIgnoreCase)) return "Insta360";
        if (folder.Length != 8 || !folder[..3].All(char.IsAsciiDigit)) return null;
        string tag = folder[3..].ToUpperInvariant();
        foreach ((string prefix, string make) in DcimTags)
            if (tag.StartsWith(prefix, StringComparison.Ordinal)) return make;
        return null;
    }

    private static readonly (string Prefix, string Make)[] DcimTags =
    [
        ("MSDCF", "Sony"), ("CANON", "Canon"), ("EOS", "Canon"), ("NIKON", "Nikon"), ("NCD", "Nikon"), ("NCZ", "Nikon"),
        ("_PANA", "Panasonic"), ("PANA", "Panasonic"), ("_FUJI", "Fujifilm"), ("FUJI", "Fujifilm"), ("OLYMP", "Olympus"),
        ("OMSYS", "OM System"), ("GOPRO", "GoPro"), ("LEICA", "Leica"), ("RICOH", "Ricoh"), ("PENTX", "Pentax"),
        ("SIGMA", "Sigma"),
    ];

    [GeneratedRegex(@"^[A-Z]\d{3}R[0-9A-Z]{3}$", RegexOptions.IgnoreCase)]
    private static partial Regex ArriReel();
}
