namespace WaterMarker.Core;

public static class ExportPaths
{
    public static string Temporary(Guid id, string targetPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(targetPath))!, $".watermarker-{id:N}.tmp");

    public static bool OwnsTemporary(ExportRecord record)
    {
        try
        {
            return Path.IsPathFullyQualified(record.TargetPath) && Path.IsPathFullyQualified(record.TemporaryPath) &&
                string.Equals(Path.GetFullPath(record.TemporaryPath), Temporary(record.Id, record.TargetPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
