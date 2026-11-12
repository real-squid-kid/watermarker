using WaterMarker.App;
using WaterMarker.Core;
using WaterMarker.Infrastructure;
using WaterMarker.Rendering;
using Xunit;

namespace WaterMarker.Stage2.Tests;

public sealed class JournalSearchTests
{
    private sealed class Codes(params string[] values) : ICodeGenerator
    {
        private readonly Queue<string> remaining = new(values);
        public string Next() => remaining.Dequeue();
    }
    private sealed class Dialogs : IDesktopDialogs
    {
        public string? OpenImage() => null;
        public string? SaveImage(ImageDocument image) => null;
        public string? ProjectName(string? currentName) => null;
    }

    private static ExportRecord Add(JournalStore store, Workspace workspace, ProjectInfo? project, int count = 1, bool ready = true)
    {
        var record = store.Reserve(new(project, "Recipient", "Email", workspace.File(Guid.NewGuid() + ".png")), "deleted.png", "PNG", count);
        store.SetPublishing(record.Id, "checksum");
        if (ready) store.SetReady(record.Id);
        return record;
    }

    [Fact]
    public void SubstringSearchFindsAllPositionsAndCopiesOnceWithLiteralCaseInsensitiveMatching()
    {
        using var workspace = new Workspace();
        var store = workspace.Store(new Codes("DZ12345678", "12DZ345678", "12345678DZ", "D12345678Z", "12ZD345678", "ABCDEFGHDZ"));
        var a = Add(store, workspace, null, count: 2);
        var b = Add(store, workspace, store.CreateProject("Project"));
        Add(store, workspace, null);
        Add(store, workspace, null);
        var incomplete = Add(store, workspace, null, ready: false);
        store.MarkInterrupted(incomplete.Id);
        store = workspace.Store();
        var matches = store.SearchCodes(" dz ");
        Assert.Equal(3, matches.Count);
        Assert.Equal(new[] { a.Id, b.Id, incomplete.Id }.Order(), matches.Select(r => r.Id).Order());
        Assert.Equal(2, matches.Single(r => r.Id == a.Id).Codes.Count);
        Assert.Equal(ExportState.Interrupted, matches.Single(r => r.Id == incomplete.Id).State);
        Assert.Equal(a.Id, Assert.Single(store.SearchCodes("12DZ")).Id);
        Assert.Equal(b.Id, Assert.Single(store.SearchCodes("12345678DZ")).Id);
        foreach (var invalid in new[] { "", " ", "%", "_", "' OR 1=1 --", "DZ123456789", "ZZZ" }) Assert.Empty(store.SearchCodes(invalid));
        Assert.NotEmpty(store.SearchCodes("D"));
    }

    [Fact]
    public async Task TypingSearchesWithoutEnterAndClearingRestoresProjectFilter()
    {
        using var workspace = new Workspace();
        var store = workspace.Store(new Codes("12DZ345678", "12ZD345678", "ABCDEFGHDZ"));
        var project = store.CreateProject("Project");
        var a = Add(store, workspace, project);
        var b = Add(store, workspace, null);
        var c = Add(store, workspace, null);
        var vm = new MainViewModel(store, new Dialogs());
        vm.SelectedFilter = vm.Filters.Single(p => p.Label == "No project");
        Assert.Equal(2, vm.Journal.Count);
        vm.SearchCode = "d";
        vm.SearchCode = " dz ";
        await vm.JournalSearchExecution;
        Assert.Equal(new[] { a.Id, c.Id }.Order(), vm.Journal.Select(r => r.Id).Order());
        Assert.Null(vm.SelectedRecord);
        vm.SearchCode = "ZD";
        await vm.JournalSearchExecution;
        Assert.Equal(b.Id, Assert.Single(vm.Journal).Id);
        Assert.Equal(b.Id, vm.SelectedRecord!.Id);
        vm.SearchCode = "ZZZ";
        await vm.JournalSearchExecution;
        Assert.Empty(vm.Journal);
        Assert.Equal("No matching record", vm.SearchStatus);
        vm.SearchCode = "";
        await vm.JournalSearchExecution;
        Assert.Equal(new[] { b.Id, c.Id }.Order(), vm.Journal.Select(r => r.Id).Order());
        Assert.Equal("", vm.SearchStatus);
        vm.SearchCode = "DZ";
        var pending = vm.JournalSearchExecution;
        vm.ClearSearchCommand.Execute(null);
        await pending;
        Assert.Equal("", vm.SearchCode);
        Assert.Equal(2, vm.Journal.Count);
        vm.ReleasePreview();
    }

    private sealed class DelayedStore(IJournalStore inner) : IJournalStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<ExportRecord> SearchCodes(string fragment)
        {
            var result = inner.SearchCodes(fragment);
            if (fragment == "DZ")
            {
                Entered.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
            }
            return result;
        }
        public IReadOnlyList<ProjectInfo> GetProjects() => inner.GetProjects();
        public ProjectInfo CreateProject(string name) => inner.CreateProject(name);
        public ProjectInfo RenameProject(Guid id, string name) => inner.RenameProject(id, name);
        public ExportRecord Reserve(ExportRequest request, string sourceName, string sourceType, int codeCount) => inner.Reserve(request, sourceName, sourceType, codeCount);
        public void SetPublishing(Guid id, string checksum) => inner.SetPublishing(id, checksum);
        public void SetReady(Guid id) => inner.SetReady(id);
        public void MarkInterrupted(Guid id) => inner.MarkInterrupted(id);
        public IReadOnlyList<ExportRecord> List(JournalFilter filter) => inner.List(filter);
        public ExportRecord? Find(string code) => inner.Find(code);
    }

    [Fact]
    public async Task SlowOlderDatabaseResultCannotOverwriteNewerSearch()
    {
        using var workspace = new Workspace();
        var store = workspace.Store(new Codes("12DZ345678", "12ZD345678"));
        Add(store, workspace, null);
        var newest = Add(store, workspace, null);
        var delayed = new DelayedStore(store);
        var vm = new MainViewModel(delayed, new Dialogs());
        vm.SearchCode = "DZ";
        var old = vm.JournalSearchExecution;
        try
        {
            await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            vm.SearchCode = "ZD";
            await vm.JournalSearchExecution;
            Assert.Equal(newest.Id, Assert.Single(vm.Journal).Id);
        }
        finally { delayed.Release.TrySetResult(); await old; }
        Assert.Equal(newest.Id, Assert.Single(vm.Journal).Id);
        vm.ReleasePreview();
    }
}
