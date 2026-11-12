using System.IO;
using WaterMarker.Core;
using Xunit;

namespace WaterMarker.Stage1.Tests;

public sealed class ExportFailureTests
{
    private sealed class FakeMedia(Action<string, CancellationToken> render) : IExportMedia
    {
        public string SourceName => "source.png";
        public string SourceType => "PNG";
        public int CodeCount => 3;
        public IReadOnlyList<string> Codes { get; private set; } = [];
        public void RenderTo(string path, IReadOnlyList<string> codes, CancellationToken cancellationToken)
        {
            Codes = codes;
            render(path, cancellationToken);
        }
        public void ValidateOutput(string path) { }
    }

    [Fact]
    public void OccupiedTargetIsNeverOverwritten()
    {
        using var workspace = new TestWorkspace();
        var target = workspace.File("copy.png");
        File.WriteAllText(target, "existing");
        var media = new FakeMedia((_, _) => throw new InvalidOperationException("Must not render"));
        var store = workspace.Store();
        Assert.Throws<IOException>(() => new ExportService(store).Export(new(null, "A", "Email", target), media, TestContext.Current.CancellationToken));
        Assert.Equal("existing", File.ReadAllText(target));
        Assert.Empty(store.List(new(true)));
        Assert.Empty(media.Codes);
    }

    [Fact]
    public void RenderFailureRemovesOnlyOwnedTemporaryFileAndKeepsReservedCodes()
    {
        using var workspace = new TestWorkspace();
        var target = workspace.File("copy.png");
        var unrelated = workspace.File("unrelated.tmp");
        File.WriteAllText(unrelated, "keep");
        var media = new FakeMedia((path, _) => { File.WriteAllText(path, "partial"); throw new IOException("Disk full"); });
        var store = workspace.Store();
        Assert.Throws<IOException>(() => new ExportService(store).Export(new(null, "A", "Email", target), media, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(target));
        var record = store.Find(media.Codes[0])!;
        Assert.Equal(ExportState.Interrupted, record.State);
        Assert.False(File.Exists(record.TemporaryPath));
        Assert.Equal("keep", File.ReadAllText(unrelated));
        Assert.Empty(store.List(new(true)));
    }

    [Fact]
    public void RaceAtPublicationCannotReplaceAnotherFile()
    {
        using var workspace = new TestWorkspace();
        var target = workspace.File("copy.png");
        var media = new FakeMedia((path, _) => { File.WriteAllText(path, "complete"); File.WriteAllText(target, "other"); });
        var store = workspace.Store();
        Assert.Throws<IOException>(() => new ExportService(store).Export(new(null, "A", "Email", target), media, TestContext.Current.CancellationToken));
        Assert.Equal("other", File.ReadAllText(target));
        store.Recover();
        Assert.Equal(ExportState.Interrupted, store.Find(media.Codes[0])!.State);
        Assert.Equal("other", File.ReadAllText(target));
    }

    [Fact]
    public void CancellationBeforePublishingDoesNotLeaveAFinalFile()
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var target = workspace.File("copy.png");
        var media = new FakeMedia((path, _) => { File.WriteAllText(path, "complete"); cancellation.Cancel(); });
        var store = workspace.Store();
        Assert.Throws<OperationCanceledException>(() => new ExportService(store).Export(new(null, "A", "Email", target), media, cancellation.Token));
        Assert.False(File.Exists(target));
        Assert.Equal(ExportState.Interrupted, store.Find(media.Codes[0])!.State);
    }

    [Fact]
    public void ProjectSnapshotIsUnaffectedByRenameDuringRendering()
    {
        using var workspace = new TestWorkspace();
        var store = workspace.Store();
        var project = store.CreateProject("Before");
        var media = new FakeMedia((path, _) => { store.RenameProject(project.Id, "After"); File.WriteAllText(path, "complete"); });
        var record = new ExportService(store).Export(new(project, "A", "Email", workspace.File("copy.png")), media, TestContext.Current.CancellationToken);
        Assert.Equal("Before", record.ProjectName);
        Assert.Equal("After", store.GetProjects()[0].Name);
        Assert.Equal(project.Id, record.ProjectId);
    }
}
