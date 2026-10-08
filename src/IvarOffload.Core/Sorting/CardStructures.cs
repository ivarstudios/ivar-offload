using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace IvarOffload.Core.Sorting;

/// <summary>
/// Recognizes the folder that holds a video card's whole structure (Sony M4ROOT, Panasonic P2 CONTENTS, RED .RDM, ...).
/// Folders with a generic name are only recognized by what they hold, so a user folder called "CONTENTS" is not
/// mistaken for a card.
/// </summary>
public static partial class CardStructures
{
    /// <summary>A P2 card keeps this file next to its CONTENTS folder.</summary>
    public const string P2LastClipFile = "LASTCLIP.TXT";

    /// <summary>A Sony card copied without its PRIVATE\M4ROOT wrapper, recognized by the folders it holds.</summary>
    public const string UnwrappedSonyDescription = "Sony card structure";

    /// <summary>
    /// The folders a Sony card (or an XDCAM disc) keeps inside M4ROOT / XDROOT. When such a card was copied without its
    /// wrapper, only these belong to it: a DCIM folder or anything else next to them keeps its own rules.
    /// </summary>
    public static readonly FrozenSet<string> SonyCardFolders =
        new[] { "CLIP", "EDIT", "GENERAL", "SUB", "TAKE", "THMBNL", "UDF" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The index and status files a Sony card (or an XDCAM disc) keeps next to those folders.</summary>
    public static readonly FrozenSet<string> SonyCardRootFiles =
        new[] { "MEDIAPRO.XML", "MEDIAPRO.BUP", "STATUS.BIN", "CUEUP.XML", "DISCMETA.XML", "INDEX.XML" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Describes the structure when <paramref name="folderName"/> is the root of a video card structure, else null.
    /// </summary>
    /// <param name="childFolders">Names of the folders directly inside it.</param>
    /// <param name="hasFile">Tells whether a file with the given name sits directly inside it.</param>
    public static string? Describe(string folderName, IReadOnlyCollection<string> childFolders, Func<string, bool> hasFile)
    {
        if (MediaRules.VideoStructureFolders.TryGetValue(folderName, out string? what)) return what;
        if (folderName.EndsWith(".RDM", StringComparison.OrdinalIgnoreCase) || folderName.EndsWith(".RDC", StringComparison.OrdinalIgnoreCase))
            return "RED card structure";
        if (folderName.Equals("CONTENTS", StringComparison.OrdinalIgnoreCase))
        {
            // A P2 card always has CLIP (the clips' metadata) next to VIDEO and AUDIO; a user folder may have one of them.
            if (Has(childFolders, "CLIP") && (Has(childFolders, "VIDEO") || Has(childFolders, "AUDIO"))) return "Panasonic P2 card structure";
            if (childFolders.Any(c => CanonClipsFolder().IsMatch(c))) return "Canon XF card structure";
        }
        // A Sony card copied without its PRIVATE\M4ROOT wrapper (or an XDCAM disc layout).
        if (Has(childFolders, "CLIP") && (Has(childFolders, "THMBNL") || Has(childFolders, "SUB")) && hasFile("MEDIAPRO.XML"))
            return UnwrappedSonyDescription;
        return null;
    }

    /// <summary>
    /// Whether the folder <paramref name="childFolder"/> directly inside a structure belongs to it. Everything inside a
    /// structure does, except next to the folders of a Sony card copied without its wrapper (see <see cref="SonyCardFolders"/>).
    /// </summary>
    public static bool Owns(string description, string childFolder) =>
        description != UnwrappedSonyDescription || SonyCardFolders.Contains(childFolder);

    /// <summary>
    /// Finds a video card structure that contains <paramref name="folder"/> or starts at it, looking at the folder and all
    /// its parents. Returns the outermost structure folder and the folder that holds the whole card, or null. A Sony card
    /// copied without its wrapper is the whole card itself: picked as the folder, it is not enclosed (null), and a folder
    /// picked inside it gets that card folder as the suggestion.
    /// </summary>
    public static (string StructureRoot, string Description, string SuggestedFolder)? FindEnclosing(
        string folder, Func<string, IReadOnlyCollection<string>> childFolders, Func<string, bool> fileExists)
    {
        (string Root, string Description)? outermost = null;
        string? below = null; // the folder directly inside p on the way down to the picked folder
        for (string? p = Path.TrimEndingDirectorySeparator(folder); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            string name = Path.GetFileName(p);
            if (name.Length == 0) break; // a drive root is never a structure
            string current = p;
            if (Describe(name, childFolders(current), n => fileExists(Path.Join(current, n))) is { } what
                && (below is null || Owns(what, below)))
                outermost = (current, what);
            below = name;
        }
        if (outermost is not { } found) return null;
        if (found.Description == UnwrappedSonyDescription)
            return found.Root.Equals(Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase)
                ? null : (found.Root, found.Description, found.Root);

        // Sony and AVCHD structures sit inside PRIVATE; the card is the folder above that.
        string suggested = Path.GetDirectoryName(found.Root) ?? found.Root;
        if (Path.GetFileName(suggested).Equals("PRIVATE", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(suggested) is { } card)
            suggested = card;
        return (found.Root, found.Description, suggested);
    }

    private static bool Has(IReadOnlyCollection<string> names, string name) =>
        names.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^CLIPS\d{3}$", RegexOptions.IgnoreCase)]
    private static partial Regex CanonClipsFolder();
}
