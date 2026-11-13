using System.IO;
using WaterMarker.App;
using WaterMarker.Core;
using WaterMarker.Rendering;
using Xunit;

namespace WaterMarker.Stage2.Tests;

public sealed class CancellationTests
{
    private sealed class Dialogs(ImageDocument image, string path) : IDesktopDialogs
    {
        public string? OpenImage() => null;
        public string? SaveImage(ImageDocument source) => path;
        public string? ProjectName(string? name) => null;
        public Task<ImageDocument?> CaptureImageAsync() => Task.FromResult<ImageDocument?>(image);
    }

    private sealed class GatedStore(IJournalStore inner, bool publishing) : IJournalStore, IDisposable
    {
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Continue { get; } = new();
        private void Wait()
        {
            Started.Set();
            if (!Continue.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Test gate timed out.");
        }
        public IReadOnlyList<ProjectInfo> GetProjects() => inner.GetProjects();
        public ProjectInfo CreateProject(string name) => inner.CreateProject(name);
        public ProjectInfo RenameProject(Guid id, string name) => inner.RenameProject(id, name);
        public ExportRecord Reserve(ExportRequest request, string sourceName, string sourceType, int codeCount)
        {
            var record = inner.Reserve(request, sourceName, sourceType, codeCount);
            if (!publishing) Wait();
            return record;
        }
        public void SetPublishing(Guid id, string checksum) { inner.SetPublishing(id, checksum); if (publishing) Wait(); }
        public void SetReady(Guid id) => inner.SetReady(id);
        public void MarkInterrupted(Guid id) => inner.MarkInterrupted(id);
        public IReadOnlyList<ExportRecord> List(JournalFilter filter) => inner.List(filter);
        public ExportRecord? Find(string code) => inner.Find(code);
        public IReadOnlyList<ExportRecord> SearchCodes(string fragment) => inner.SearchCodes(fragment);
        public void Dispose() { Continue.Set(); Started.Dispose(); Continue.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseCancelsRenderingButWaitsForPublication(bool publishing)
    {
        using var workspace = new Workspace();
        var store = workspace.Store();
        using var gate = new GatedStore(store, publishing);
        var image = await StaWorker.Run(() => ImageDocument.FromCapture(CaptureTests.Pattern(new(0, 0, 640, 480))));
        var target = workspace.File("copy.png");
        var vm = new MainViewModel(gate, new Dialogs(image, target)) { Recipient = "A" };
        await vm.CaptureCommand.ExecuteAsync();
        var operation = vm.WatermarkCommand.ExecuteAsync();
        try
        {
            var token = TestContext.Current.CancellationToken;
            Assert.True(await Task.Run(() => gate.Started.Wait(TimeSpan.FromSeconds(10), token), token));
            Assert.True(vm.IsExporting);
            var preview = vm.Preview;
            var dropped = workspace.File("drop-during-export.png");
            File.WriteAllText(dropped, "Must not be decoded while an export is running.");
            await vm.OpenDroppedFilesAsync([dropped]);
            Assert.Same(preview, vm.Preview);
            Assert.True(vm.IsBusy);
            Assert.Equal(!publishing, vm.CancelCommand.CanExecute(null));
            var close = vm.StopForCloseAsync();
            Assert.False(close.IsCompleted);
            gate.Continue.Set();
            await close;
            Assert.False(vm.IsExporting);
            Assert.False(vm.IsBusy);
            Assert.Equal(publishing, File.Exists(target));
            Assert.Equal(publishing ? "Copy saved." : "Cancelled.", vm.Status);
            Assert.Equal("A", vm.Recipient);
            Assert.NotNull(vm.Preview);
            Assert.Empty(Directory.GetFiles(workspace.Root, ".watermarker-*.tmp"));
        }
        finally { gate.Continue.Set(); await operation; vm.ReleasePreview(); }
    }

    [Fact]
    public void CancellationAndPublicationUseOneBoundary()
    {
        using var cancelled = new ExportControl();
        Assert.True(cancelled.TryCancel());
        Assert.Throws<OperationCanceledException>(() => cancelled.BeginPublishing(TestContext.Current.CancellationToken));
        using var publishing = new ExportControl();
        publishing.BeginPublishing(TestContext.Current.CancellationToken);
        Assert.False(publishing.CanCancel);
        Assert.False(publishing.TryCancel());
        Assert.False(publishing.Token.IsCancellationRequested);
    }

    private sealed class ValidationWaiter : IExportMedia, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public string SourceName => "source.mp4";
        public string SourceType => "Video";
        public int CodeCount => 1;
        public void RenderTo(string path, IReadOnlyList<string> codes, CancellationToken token) => File.WriteAllText(path, "temporary video");
        public void ValidateOutput(string path) => throw new InvalidOperationException("Cancellation must be passed to validation.");
        public void ValidateOutput(string path, CancellationToken token)
        {
            Entered.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("Cancellation did not reach validation.");
        }
        public void Dispose() => Entered.Dispose();
    }

    [Fact]
    public async Task CancellationDuringValidationInterruptsExportAndRemovesTemporaryFile()
    {
        using var workspace = new Workspace();
        var store = workspace.Store();
        using var media = new ValidationWaiter();
        using var control = new ExportControl();
        ExportRecord? reserved = null;
        var target = workspace.File("cancelled.mp4");
        var service = new ExportService(store, (phase, record) => { if (phase == ExportCheckpoint.Reserved) reserved = record; });
        var operation = Task.Run(() => service.Export(new(null, "A", "Email", target), media, control: control), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(await Task.Run(() => media.Entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
            Assert.True(control.TryCancel());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.NotNull(reserved);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(reserved.TemporaryPath));
            Assert.Equal(ExportState.Interrupted, store.Find(reserved.Codes[0])!.State);
        }
        finally { control.TryCancel(); }
    }
}
