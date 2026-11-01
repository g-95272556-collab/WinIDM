namespace WinIDM.Engine;

/// <summary>
/// Maps file extensions to categories (Video, Audio, Image, Document, Archive, Program, Other)
/// and the default folder for each category under the download folder.
/// </summary>
public static class DownloadCategories
{
    public const string Video = "Video";
    public const string Audio = "Audio";
    public const string Image = "Image";
    public const string Document = "Document";
    public const string Archive = "Archive";
    public const string Program = "Program";
    public const string Other = "Other";

    private static readonly IReadOnlyDictionary<string, string> ExtensionToCategory = BuildMap();

    /// <summary>All categories in display order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Video, Audio, Image, Document, Archive, Program, Other];

    /// <summary>Map of extension (with dot, lowercase) to category.</summary>
    public static IReadOnlyDictionary<string, string> Map => ExtensionToCategory;

    /// <summary>Category for a file name; <see cref="Other"/> if the extension is unknown.</summary>
    public static string GetCategory(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return Other;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext.Length > 0 && ExtensionToCategory.TryGetValue(ext, out var category) ? category : Other;
    }

    /// <summary>Default folder name for a category (same as the category name).</summary>
    public static string DefaultFolderName(string category) =>
        All.Contains(category) ? category : Other;

    /// <summary>Full folder for a category under <paramref name="rootDirectory"/>.</summary>
    public static string GetSaveDirectory(string rootDirectory, string category) =>
        Path.Combine(rootDirectory, DefaultFolderName(category));

    private static IReadOnlyDictionary<string, string> BuildMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string category, params string[] extensions)
        {
            foreach (var ext in extensions) map[ext] = category;
        }

        Add(Video, ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".m3u8", ".3gp");
        Add(Audio, ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma", ".opus");
        Add(Image, ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".tif", ".tiff", ".heic");
        Add(Document, ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt", ".ods", ".csv", ".epub", ".md");
        Add(Archive, ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso");
        Add(Program, ".exe", ".msi", ".apk", ".dmg", ".deb", ".appimage", ".bat", ".cmd", ".ps1", ".jar");

        return map;
    }
}
