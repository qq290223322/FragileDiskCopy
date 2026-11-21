namespace FragileDiskCopy;

[Flags]
internal enum FileTypeCategory
{
    AllFiles = 0,
    Images = 1,
    Videos = 2,
    Documents = 4
}

internal static class FileTypeFilter
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif",
        ".raw", ".raf", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".psd", ".svg"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".flv", ".webm", ".m4v", ".mts", ".m2ts",
        ".mpg", ".mpeg", ".3gp", ".ts", ".vob", ".rm", ".rmvb"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".csv",
        ".tsv", ".md", ".odt", ".ods", ".odp", ".epub", ".json", ".xml", ".html", ".htm"
    };

    public static bool Matches(string path, FileTypeCategory category)
    {
        if (category == FileTypeCategory.AllFiles)
            return true;

        var extension = Path.GetExtension(path);
        return ((category & FileTypeCategory.Images) != 0 && ImageExtensions.Contains(extension)) ||
               ((category & FileTypeCategory.Videos) != 0 && VideoExtensions.Contains(extension)) ||
               ((category & FileTypeCategory.Documents) != 0 && DocumentExtensions.Contains(extension));
    }
}
