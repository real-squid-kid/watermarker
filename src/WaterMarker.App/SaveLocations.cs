using System.IO;
using System.Text.Json;

namespace WaterMarker.App;

public sealed class SaveLocations
{
    private readonly string settingsPath;
    private string? lastDirectory;

    public SaveLocations(string dataDirectory)
    {
        settingsPath = Path.Combine(dataDirectory, "save-location.json");
        try
        {
            if (File.Exists(settingsPath)) lastDirectory = JsonSerializer.Deserialize<string>(File.ReadAllText(settingsPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public string InitialDirectory(string sourcePath)
    {
        if (lastDirectory is not null && Directory.Exists(lastDirectory)) return lastDirectory;
        if (Path.IsPathFullyQualified(sourcePath) && File.Exists(sourcePath)) return Path.GetDirectoryName(sourcePath)!;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    public void Remember(string selectedFile)
    {
        lastDirectory = Path.GetDirectoryName(Path.GetFullPath(selectedFile));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var temporary = settingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(lastDirectory));
            File.Move(temporary, settingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* An unwritable preference must not prevent exporting. */ }
    }
}
