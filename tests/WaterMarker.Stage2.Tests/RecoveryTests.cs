using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WaterMarker.Core;
using Xunit;

namespace WaterMarker.Stage2.Tests;

public sealed class RecoveryTests
{
    [Theory]
    [InlineData("Reserved", false)]
    [InlineData("Writing", false)]
    [InlineData("Rendered", false)]
    [InlineData("PublicationStarting", false)]
    [InlineData("Publishing", true)]
    [InlineData("Published", true)]
    [InlineData("Ready", true)]
    [InlineData("Kill:Publishing", true)]
    [InlineData("Kill:Published", true)]
    public async Task RestartRecoversAfterRealProcessTermination(string phase, bool ready)
    {
        using var workspace = new Workspace();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "FaultHost", "WaterMarker.FaultHost.dll"));
        start.ArgumentList.Add(workspace.Root);
        start.ArgumentList.Add(phase);
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (phase.StartsWith("Kill:", StringComparison.Ordinal))
            {
                while (!System.IO.File.Exists(workspace.File("checkpoint.json")))
                {
                    Assert.False(process.HasExited, "Fault host exited before the checkpoint.");
                    await Task.Delay(25, timeout.Token);
                }
                // Wait for a complete marker, then terminate the actual process rather than throw in-process.
                while (true)
                {
                    try { _ = JsonSerializer.Deserialize<ExportRecord>(System.IO.File.ReadAllText(workspace.File("checkpoint.json"))); break; }
                    catch (Exception ex) when (ex is JsonException or IOException) { await Task.Delay(25, timeout.Token); }
                }
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(timeout.Token);
            Assert.NotEqual(0, process.ExitCode);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(TestContext.Current.CancellationToken); } }
        var record = JsonSerializer.Deserialize<ExportRecord>(System.IO.File.ReadAllText(workspace.File("checkpoint.json")))!;
        var store = workspace.Store();
        var result = store.Recover();
        Assert.Equal(0, result.Pending);
        var restored = store.Find(record.Codes[0])!;
        Assert.Equal(ready ? ExportState.Ready : ExportState.Interrupted, restored.State);
        Assert.False(System.IO.File.Exists(record.TemporaryPath));
        Assert.Equal(ready, System.IO.File.Exists(record.TargetPath));
        Assert.Equal(ready ? 1 : 0, store.List(new(true)).Count);
        if (ready) Assert.Equal(restored.Checksum, ExportService.FileChecksum(record.TargetPath));
        foreach (var code in record.Codes) Assert.Equal(record.Id, store.Find(code)!.Id);
        Assert.Equal(new RecoveryResult(0, 0, 0), store.Recover());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignFileOrDirectoryOccupyingFinalNameIsPreserved(bool directory)
    {
        using var workspace = new Workspace();
        var store = workspace.Store();
        var record = workspace.Reserve(store);
        File.WriteAllText(record.TemporaryPath, "complete copy");
        store.SetPublishing(record.Id, ExportService.FileChecksum(record.TemporaryPath));
        if (directory) Directory.CreateDirectory(record.TargetPath);
        else File.WriteAllText(record.TargetPath, "foreign file");
        var unrelated = workspace.File("unrelated.tmp");
        File.WriteAllText(unrelated, "keep");
        Assert.Equal(new RecoveryResult(0, 1, 0), store.Recover());
        Assert.Equal(ExportState.Interrupted, store.Find(record.Codes[0])!.State);
        Assert.False(File.Exists(record.TemporaryPath));
        Assert.Equal("keep", File.ReadAllText(unrelated));
        if (directory) Assert.True(Directory.Exists(record.TargetPath));
        else Assert.Equal("foreign file", File.ReadAllText(record.TargetPath));
        Assert.Empty(store.List(new(true)));
    }

    [Fact]
    public void CorruptedTempIsNotPublishedAndItsCodesAreNeverReassigned()
    {
        using var workspace = new Workspace();
        var store = workspace.Store();
        var record = workspace.Reserve(store);
        File.WriteAllText(record.TemporaryPath, "complete");
        store.SetPublishing(record.Id, ExportService.FileChecksum(record.TemporaryPath));
        File.WriteAllText(record.TemporaryPath, "damaged");
        Assert.Equal(new RecoveryResult(0, 1, 0), store.Recover());
        Assert.False(File.Exists(record.TargetPath));
        Assert.Throws<InvalidOperationException>(() => store.SetPublishing(record.Id, "hash"));
        var generator = new FirstCollision(record.Codes[0]);
        var next = workspace.Store(generator).Reserve(new(null, "B", "Email", workspace.File("second.png")), "s.png", "PNG", 1);
        Assert.NotEqual(record.Codes[0], next.Codes[0]);
        Assert.Equal(record.Id, store.Find(record.Codes[0])!.Id);
    }

    private sealed class FirstCollision(string first) : ICodeGenerator
    {
        private bool used;
        public string Next() { if (!used) { used = true; return first; } return new CodeGenerator().Next(); }
    }

    [Fact]
    public void LockedTempRemainsPendingAndCanBeRecoveredLater()
    {
        using var workspace = new Workspace();
        var store = workspace.Store();
        var record = workspace.Reserve(store);
        File.WriteAllText(record.TemporaryPath, "partial");
        using (var locked = new FileStream(record.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(new RecoveryResult(0, 0, 1), store.Recover());
            Assert.Equal(ExportState.Rendering, store.Find(record.Codes[0])!.State);
        }
        Assert.Equal(new RecoveryResult(0, 1, 0), store.Recover());
        Assert.False(File.Exists(record.TemporaryPath));
    }

    [Fact]
    public void DatabaseCannotAuthorizeDeletionOfAnUnrelatedFile()
    {
        using var workspace = new Workspace();
        var store = workspace.Store();
        var record = workspace.Reserve(store);
        var foreign = workspace.File("private-original.png");
        File.WriteAllText(foreign, "keep original");
        using (var connection = new SqliteConnection($"Data Source={workspace.File("journal.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE exports SET temporary_path=$path WHERE id=$id";
            command.Parameters.AddWithValue("$path", foreign);
            command.Parameters.AddWithValue("$id", record.Id.ToString());
            command.ExecuteNonQuery();
        }
        Assert.Equal(new RecoveryResult(0, 1, 0), store.Recover());
        Assert.Equal("keep original", File.ReadAllText(foreign));
    }

    [Fact]
    public void PublishedCopyStaysSearchableWhileTemporaryCleanupIsBlocked()
    {
        using var workspace = new Workspace();
        var store = workspace.Store();
        var record = workspace.Reserve(store);
        File.WriteAllText(record.TemporaryPath, "complete copy");
        var checksum = ExportService.FileChecksum(record.TemporaryPath);
        store.SetPublishing(record.Id, checksum);
        File.Copy(record.TemporaryPath, record.TargetPath);
        using (var locked = new FileStream(record.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(new RecoveryResult(0, 0, 1), store.Recover());
            Assert.Equal(ExportState.Publishing, store.Find(record.Codes[0])!.State);
            Assert.Equal(checksum, ExportService.FileChecksum(record.TargetPath));
        }
        Assert.Equal(new RecoveryResult(1, 0, 0), store.Recover());
        Assert.Equal(ExportState.Ready, store.Find(record.Codes[0])!.State);
        Assert.False(File.Exists(record.TemporaryPath));
    }
}
