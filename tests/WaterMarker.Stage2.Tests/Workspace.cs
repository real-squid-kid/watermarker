using System.IO;
using WaterMarker.Core;
using WaterMarker.Infrastructure;

namespace WaterMarker.Stage2.Tests;

internal sealed class Workspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "WaterMarker.Stage2.Tests", Guid.NewGuid().ToString("N"));
    public Workspace() => Directory.CreateDirectory(Root);
    public string File(string name) => Path.Combine(Root, name);
    public JournalStore Store(ICodeGenerator? codes = null) => new(File("journal.db"), codes);
    public ExportRecord Reserve(JournalStore store) => store.Reserve(new(null, "A", "Email", File("copy.png")), "source.png", "PNG", 3);
    public void Dispose() => Directory.Delete(Root, true);
}
