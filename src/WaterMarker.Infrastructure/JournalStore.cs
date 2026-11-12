using System.Globalization;
using Microsoft.Data.Sqlite;
using WaterMarker.Core;

namespace WaterMarker.Infrastructure;

public sealed class JournalStore : IJournalStore, IRecoveryStore
{
    private readonly string connectionString;
    private readonly ICodeGenerator codes;

    public JournalStore(string databasePath, ICodeGenerator? codes = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true, Pooling = false }.ToString();
        this.codes = codes ?? new CodeGenerator();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var version = Command(connection, "PRAGMA user_version");
        if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) > 1)
            throw new InvalidOperationException("This database requires a newer WaterMarker version.");
        using var transaction = connection.BeginTransaction();
        using var command = Command(connection, """
            CREATE TABLE IF NOT EXISTS projects (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS exports (
                id TEXT PRIMARY KEY, project_id TEXT REFERENCES projects(id), project_name TEXT,
                recipient TEXT NOT NULL, channel TEXT NOT NULL, exported_at TEXT,
                source_name TEXT NOT NULL, source_type TEXT NOT NULL,
                target_path TEXT NOT NULL, temporary_path TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Rendering','Publishing','Ready','Interrupted')),
                checksum TEXT, created_at TEXT NOT NULL,
                CHECK ((project_id IS NULL) = (project_name IS NULL)));
            CREATE TABLE IF NOT EXISTS watermark_codes (
                code TEXT PRIMARY KEY CHECK(length(code) = 10),
                export_id TEXT NOT NULL REFERENCES exports(id), grid_order INTEGER NOT NULL,
                UNIQUE(export_id, grid_order));
            CREATE INDEX IF NOT EXISTS exports_project ON exports(project_id, exported_at DESC);
            CREATE INDEX IF NOT EXISTS codes_export ON watermark_codes(export_id, grid_order);
            PRAGMA user_version = 1;
            """);
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<ProjectInfo> GetProjects()
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT id, name FROM projects ORDER BY name_key");
        using var reader = command.ExecuteReader();
        var result = new List<ProjectInfo>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        return result;
    }

    public ProjectInfo CreateProject(string name) => SaveProject(Guid.NewGuid(), name, create: true);
    public ProjectInfo RenameProject(Guid id, string name) => SaveProject(id, name, create: false);

    private ProjectInfo SaveProject(Guid id, string name, bool create)
    {
        name = ProjectNames.Clean(name);
        using var connection = Open();
        using var command = Command(connection, create
            ? "INSERT INTO projects(id, name, name_key) VALUES ($id, $name, $key)"
            : "UPDATE projects SET name=$name, name_key=$key WHERE id=$id",
            ("$id", id.ToString()), ("$name", name), ("$key", ProjectNames.Key(name)));
        try
        {
            if (command.ExecuteNonQuery() != 1) throw new ArgumentException("Project no longer exists.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ArgumentException("A project with this name already exists.", ex);
        }
        return new(id, name);
    }

    public ExportRecord Reserve(ExportRequest request, string sourceName, string sourceType, int codeCount)
    {
        request = request.Validate();
        if (codeCount < 1 || codeCount > 100_000) throw new ArgumentOutOfRangeException(nameof(codeCount));
        var id = Guid.NewGuid();
        var temporaryPath = ExportPaths.Temporary(id, request.TargetPath);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = Command(connection, """
            INSERT INTO exports (id, project_id, project_name, recipient, channel, source_name,
                source_type, target_path, temporary_path, state, created_at)
            VALUES ($id, $project, $name, $recipient, $channel, $source, $type, $target, $temp, 'Rendering', $created)
            """, ("$id", id.ToString()), ("$project", request.Project?.Id.ToString()),
            ("$name", request.Project?.Name), ("$recipient", request.Recipient), ("$channel", request.Channel),
            ("$source", sourceName), ("$type", sourceType), ("$target", request.TargetPath),
            ("$temp", temporaryPath), ("$created", DateTimeOffset.UtcNow.ToString("O"))))
        {
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
        var reserved = new List<string>();
        for (var i = 0; i < codeCount; i++)
        {
            var inserted = false;
            for (var attempt = 0; attempt < 1000 && !inserted; attempt++)
            {
                var code = codes.Next();
                if (!CodeGenerator.IsValid(code)) throw new InvalidOperationException("Invalid generated code.");
                using var command = Command(connection, """
                    INSERT INTO watermark_codes(code, export_id, grid_order) VALUES($code, $id, $order)
                    ON CONFLICT(code) DO NOTHING
                    """, ("$code", code), ("$id", id.ToString()), ("$order", i));
                command.Transaction = transaction;
                inserted = command.ExecuteNonQuery() == 1;
                if (inserted) reserved.Add(code);
            }
            if (!inserted) throw new InvalidOperationException("Unable to reserve unique codes.");
        }
        transaction.Commit();
        return new(id, request.Project?.Id, request.Project?.Name, request.Recipient, request.Channel,
            null, sourceName, sourceType, request.TargetPath, temporaryPath, ExportState.Rendering, null, reserved);
    }

    public void SetPublishing(Guid id, string checksum) => Update(id, "state='Publishing', checksum=$value", checksum, "state='Rendering'");
    public void SetReady(Guid id) => Update(id, "state='Ready', exported_at=$value", DateTimeOffset.UtcNow.ToString("O"), "state='Publishing'");
    public void MarkInterrupted(Guid id) => Update(id, "state='Interrupted', checksum=$value", null, "state IN ('Rendering','Publishing')");

    private void Update(Guid id, string assignment, string? value, string expectedState)
    {
        using var connection = Open();
        using var command = Command(connection, $"UPDATE exports SET {assignment} WHERE id=$id AND {expectedState}", ("$id", id.ToString()), ("$value", value));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Export record no longer exists.");
    }

    public IReadOnlyList<ExportRecord> List(JournalFilter filter) => ReadRecords(
        "state='Ready'" + (filter.AllProjects ? "" : filter.ProjectId is null ? " AND project_id IS NULL" : " AND project_id=$project"),
        ("$project", filter.ProjectId?.ToString()));

    public ExportRecord? Find(string code)
    {
        code = CodeGenerator.Normalize(code);
        if (!CodeGenerator.IsValid(code)) return null;
        return ReadRecords("id IN (SELECT export_id FROM watermark_codes WHERE code=$code)", ("$code", code)).SingleOrDefault();
    }

    public IReadOnlyList<ExportRecord> SearchCodes(string fragment)
    {
        fragment = CodeGenerator.Normalize(fragment);
        if (fragment.Length is < 1 or > CodeGenerator.Length || !fragment.All(CodeGenerator.Alphabet.Contains)) return [];
        // EXISTS returns each copy once, even when several of its codes contain the fragment.
        // instr is a literal substring search: no LIKE wildcard or SQL syntax in user input.
        return ReadRecords("EXISTS (SELECT 1 FROM watermark_codes WHERE export_id=exports.id AND instr(code, $fragment)>0)",
            ("$fragment", fragment));
    }

    private IReadOnlyList<ExportRecord> ReadRecords(string where, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = Command(connection, $"SELECT * FROM exports WHERE {where} ORDER BY exported_at DESC, created_at DESC", parameters);
        using var reader = command.ExecuteReader();
        var result = new List<ExportRecord>();
        while (reader.Read())
        {
            string? Optional(string name) => reader[name] is DBNull ? null : (string)reader[name];
            var id = Guid.Parse(reader.GetString(reader.GetOrdinal("id")));
            result.Add(new(id, Optional("project_id") is { } project ? Guid.Parse(project) : null,
                Optional("project_name"), (string)reader["recipient"], (string)reader["channel"],
                Optional("exported_at") is { } time ? DateTimeOffset.Parse(time, CultureInfo.InvariantCulture) : null,
                (string)reader["source_name"], (string)reader["source_type"], (string)reader["target_path"],
                (string)reader["temporary_path"], Enum.Parse<ExportState>((string)reader["state"]), Optional("checksum"), []));
        }
        reader.Close();
        for (var i = 0; i < result.Count; i++)
        {
            using var codeCommand = Command(connection, "SELECT code FROM watermark_codes WHERE export_id=$id ORDER BY grid_order", ("$id", result[i].Id.ToString()));
            using var codeReader = codeCommand.ExecuteReader();
            var allCodes = new List<string>();
            while (codeReader.Read()) allCodes.Add(codeReader.GetString(0));
            result[i] = result[i] with { Codes = allCodes };
        }
        return result;
    }

    public IReadOnlyList<ExportRecord> GetIncomplete() => ReadRecords("state IN ('Rendering','Publishing')");
    public RecoveryResult Recover() => new ExportRecovery(this).Recover();
}
