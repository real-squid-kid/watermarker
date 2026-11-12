using System.Security.Cryptography;

namespace WaterMarker.Core;

public sealed record ProjectInfo(Guid Id, string Name)
{
    public override string ToString() => Name;
}

public enum ExportState { Rendering, Publishing, Ready, Interrupted }

public sealed record ExportRecord(
    Guid Id, Guid? ProjectId, string? ProjectName, string Recipient, string Channel,
    DateTimeOffset? ExportedAt, string SourceName, string SourceType,
    string TargetPath, string TemporaryPath, ExportState State, string? Checksum,
    IReadOnlyList<string> Codes)
{
    public string ProjectDisplay => ProjectName ?? "No project";
    public string FileName => Path.GetFileName(TargetPath);
    public string ExportedAtDisplay => ExportedAt?.ToLocalTime().ToString("g") ?? "Incomplete save";
}

public sealed record JournalFilter(bool AllProjects, Guid? ProjectId = null);

public sealed record ExportRequest(ProjectInfo? Project, string Recipient, string Channel, string TargetPath)
{
    public ExportRequest Validate()
    {
        ValidateFields(Recipient, Channel);
        if (string.IsNullOrWhiteSpace(TargetPath))
            throw new ArgumentException("Choose an output file.");
        return this with { Recipient = Recipient.Trim(), Channel = Channel.Trim(), TargetPath = Path.GetFullPath(TargetPath) };
    }

    public static void ValidateFields(string recipient, string channel)
    {
        if (string.IsNullOrWhiteSpace(recipient))
            throw new ArgumentException("Enter a recipient.");
        if (string.IsNullOrWhiteSpace(channel))
            throw new ArgumentException("Enter a channel.");
    }
}

public static class ProjectNames
{
    public static string Clean(string name) => string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("Enter a project name.") : name.Trim();

    public static string Key(string name) => Clean(name).ToUpperInvariant();
}

public interface ICodeGenerator
{
    string Next();
}

public sealed class CodeGenerator : ICodeGenerator
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 10;
    public string Next() => string.Create(Length, 0, static (span, _) =>
    {
        for (var i = 0; i < span.Length; i++)
            span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
    });

    public static string Normalize(string code) => code.Trim().ToUpperInvariant();
    public static bool IsValid(string code) => code.Length == Length && code.All(Alphabet.Contains);
}
