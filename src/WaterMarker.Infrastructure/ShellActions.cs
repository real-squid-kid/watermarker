using System.Diagnostics;

namespace WaterMarker.Infrastructure;

public static class ShellActions
{
    public static void OpenFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The copy has been moved or deleted.");
        OpenPath(path);
    }

    public static void OpenFolder(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The output folder no longer exists.");
        OpenPath(folder);
    }

    private static void OpenPath(string path)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        Process.Start(start);
    }
}
