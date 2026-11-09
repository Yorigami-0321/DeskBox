namespace DeskBox.Helpers;

internal static class FileSizeFormatter
{
    /// <summary>Formats a byte count as a compact, culture-aware size string.</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[0]}"
            : $"{size:0.##} {units[unit]}";
    }
}
