using System.Collections.Frozen;

namespace IvarOffload.Core.Sorting;

/// <summary>
/// Photogrammetry processing folders that have no project file of their own, recognized by their layout. Like a folder
/// with a project file, everything in them stays: the orthomosaic, the DSM and the input images belong to the project.
/// </summary>
public static class ProcessingProjects
{
    /// <summary>The working and output folders OpenDroneMap (and WebODM) write next to the project's images folder.</summary>
    private static readonly FrozenSet<string> OdmFolders = new[]
    {
        "opensfm", "odm_orthophoto", "odm_dem", "odm_georeferencing", "odm_texturing", "odm_meshing", "odm_filterpoints",
        "odm_report", "odm_25dgeoreferencing", "odm_25dmeshing", "odm_25dtexturing", "entwine_pointcloud",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>DJI Terra's 2D reconstruction results in its map folder.</summary>
    private static readonly string[] TerraMapFiles = ["result.tif", "dsm.tif"];

    /// <summary>
    /// Describes the processing project a folder is by its layout, or null.
    /// </summary>
    /// <param name="subfolders">The names of the folders directly inside a folder given relative to it ("" for itself, "models\pc").</param>
    /// <param name="fileExists">Whether a file exists at a path relative to the folder ("map\result.tif").</param>
    /// <returns>The kind of project ("OpenDroneMap project") and what gave it away ("odm_dem folder").</returns>
    public static (string Description, string Evidence)? Describe(Func<string, IReadOnlyCollection<string>> subfolders, Func<string, bool> fileExists)
    {
        IReadOnlyCollection<string> childFolders = subfolders("");

        // OpenDroneMap / WebODM: its own folders next to the input images (or several of them in a downloaded result).
        var odm = childFolders.Where(OdmFolders.Contains).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (odm.Count >= 2 || odm.Count == 1 && Child(childFolders, "images") is not null)
            return ("OpenDroneMap project", $"{odm[0]} folder");

        // DJI Terra: its results, not just folders called map or models (a shoot can have "Map" and "Models" folders):
        // map\result.tif or map\dsm.tif, or a terra_* output folder (terra_las, terra_b3dms, terra_obj, ...) in
        // lidars\ or models\pc\0\, models\mesh\0\.
        if (Child(childFolders, "map") is { } map && TerraMapFiles.Select(n => Path.Join(map, n)).FirstOrDefault(fileExists) is { } result)
            return ("DJI Terra project", result);
        if (Child(childFolders, "lidars") is { } lidars && TerraOutput(subfolders, lidars) is { } lidarOutput)
            return ("DJI Terra project", $"{lidarOutput} folder");
        if (Child(childFolders, "models") is { } models)
            foreach (string kind in subfolders(models).Where(k => k.Equals("pc", StringComparison.OrdinalIgnoreCase) || k.Equals("mesh", StringComparison.OrdinalIgnoreCase)))
            {
                string kindPath = Path.Join(models, kind);
                if (TerraOutput(subfolders, kindPath) is { } output) return ("DJI Terra project", $"{output} folder");
                foreach (string block in subfolders(kindPath)) // numbered blocks: models\pc\0\terra_las
                    if (TerraOutput(subfolders, Path.Join(kindPath, block)) is { } blockOutput)
                        return ("DJI Terra project", $"{blockOutput} folder");
            }
        return null;
    }

    /// <summary>The relative path of a terra_* output folder directly inside <paramref name="folder"/>, or null.</summary>
    private static string? TerraOutput(Func<string, IReadOnlyCollection<string>> subfolders, string folder) =>
        subfolders(folder).Where(n => n.StartsWith("terra_", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase)
            .Select(n => Path.Join(folder, n)).FirstOrDefault();

    private static string? Child(IReadOnlyCollection<string> names, string name) =>
        names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
}
