using System.IO;
using Microsoft.Data.Sqlite;
using WaterMarker.Core;
using WaterMarker.Infrastructure;
using Xunit;

namespace WaterMarker.Stage1.Tests;

public sealed class TestWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "WaterMarker.Tests", Guid.NewGuid().ToString("N"));
    public TestWorkspace() => Directory.CreateDirectory(Root);
    public string File(string name) => Path.Combine(Root, name);
    public JournalStore Store(ICodeGenerator? generator = null) => new(File("journal.db"), generator);
    public void Dispose() => Directory.Delete(Root, true);
}

public sealed class StoreTests
{
    [Fact]
    public void ProjectNamesAreTrimmedUniqueAndHaveStableIds()
    {
        using var workspace = new TestWorkspace();
        var store = workspace.Store();
        Assert.Empty(store.GetProjects());
        Assert.Throws<ArgumentException>(() => store.CreateProject("  "));
        var first = store.CreateProject("  Дизайн упаковки  ");
        Assert.Equal("Дизайн упаковки", first.Name);
        Assert.Throws<ArgumentException>(() => store.CreateProject("дизайн УПАКОВКИ"));
        var other = store.CreateProject("Photo Review");
        Assert.Throws<ArgumentException>(() => store.RenameProject(other.Id, " ДИЗАЙН УПАКОВКИ "));
        Assert.Equal(first.Id, store.RenameProject(first.Id, "Новое имя").Id);
        Assert.Equal(2, workspace.Store().GetProjects().Count);
    }

    [Fact]
    public void CodesAreCryptographicTenCharacterValuesFromTheSpecifiedAlphabet()
    {
        var generator = new CodeGenerator();
        for (var i = 0; i < 500; i++) Assert.True(CodeGenerator.IsValid(generator.Next()));
        Assert.Equal("0123456789ABCDEFGHJKMNPQRSTVWXYZ", CodeGenerator.Alphabet);
    }

    private sealed class CollisionGenerator : ICodeGenerator
    {
        private int count;
        public string Next() => ++count <= 2 ? "0000000000" : "1111111111";
    }

    [Fact]
    public void CollisionsRetryAndTheDatabaseAlsoRejectsDuplicates()
    {
        using var workspace = new TestWorkspace();
        var store = workspace.Store(new CollisionGenerator());
        var record = store.Reserve(new(null, "A", "Email", workspace.File("out.png")), "source.png", "PNG", 2);
        Assert.Equal(new[] { "0000000000", "1111111111" }, record.Codes);
        using var connection = new SqliteConnection($"Data Source={workspace.File("journal.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO watermark_codes VALUES ('0000000000', $id, 2)";
        command.Parameters.AddWithValue("$id", record.Id.ToString());
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    private sealed class InvalidGenerator : ICodeGenerator
    {
        public string Next() => "INVALID";
    }

    [Fact]
    public void FailedCodeReservationRollsBackTheEntireExport()
    {
        using var workspace = new TestWorkspace();
        var store = workspace.Store(new InvalidGenerator());
        Assert.Throws<InvalidOperationException>(() => store.Reserve(new(null, "A", "Email", workspace.File("out.png")), "s.png", "PNG", 2));
        using var connection = new SqliteConnection($"Data Source={workspace.File("journal.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM exports";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void JournalFiltersRenameSnapshotsAndGlobalSearchSurviveRestart()
    {
        using var workspace = new TestWorkspace();
        var store = workspace.Store();
        var first = store.CreateProject("Дизайн упаковки");
        var second = store.CreateProject("Photo Review");
        var records = new List<ExportRecord>();
        foreach (var project in new ProjectInfo?[] { first, second, null })
        {
            var record = store.Reserve(new(project, "Recipient", "Telegram", workspace.File(Guid.NewGuid() + ".png")), "deleted.png", "PNG", 4);
            store.SetPublishing(record.Id, "hash");
            store.SetReady(record.Id);
            records.Add(record);
        }
        store.RenameProject(first.Id, "Новое имя");
        store = workspace.Store();
        Assert.Equal(3, store.List(new(true)).Count);
        Assert.Equal(records[2].Id, store.List(new(true))[0].Id);
        Assert.Equal("Дизайн упаковки", Assert.Single(store.List(new(false, first.Id))).ProjectName);
        Assert.Null(Assert.Single(store.List(new(false))).ProjectId);
        Assert.Equal(second.Id, store.Find(" " + records[1].Codes[0].ToLowerInvariant() + " ")!.ProjectId);
        Assert.Null(store.Find(records[0].Codes[0][1..]));
        Assert.Null(store.Find("ZZZZZZZZZZ"));
        Assert.Equal("Новое имя", store.GetProjects().Single(p => p.Id == first.Id).Name);
        Assert.Equal(12, records.SelectMany(r => r.Codes).Distinct().Count());
    }

    [Fact]
    public void PublishingIsSearchableAndInterruptedCodesStayReserved()
    {
        using var workspace = new TestWorkspace();
        var store = workspace.Store();
        var record = store.Reserve(new(null, "A", "Other service", workspace.File("out.png")), "s.png", "PNG", 2);
        store.SetPublishing(record.Id, "hash");
        Assert.Equal(ExportState.Publishing, store.Find(record.Codes[0])!.State);
        Assert.Empty(store.List(new(true)));
        store.MarkInterrupted(record.Id);
        Assert.Equal(record.Codes, store.Find(record.Codes[0])!.Codes);
        Assert.Empty(store.List(new(true)));
    }
}
