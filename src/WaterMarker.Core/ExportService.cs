using System.Security.Cryptography;

namespace WaterMarker.Core;

public interface IJournalStore
{
    IReadOnlyList<ProjectInfo> GetProjects();
    ProjectInfo CreateProject(string name);
    ProjectInfo RenameProject(Guid id, string name);
    ExportRecord Reserve(ExportRequest request, string sourceName, string sourceType, int codeCount);
    void SetPublishing(Guid id, string checksum);
    void SetReady(Guid id);
    void MarkInterrupted(Guid id);
    IReadOnlyList<ExportRecord> List(JournalFilter filter);
    ExportRecord? Find(string code);
}

public interface IExportMedia
{
    string SourceName { get; }
    string SourceType { get; }
    int CodeCount { get; }
    void RenderTo(string path, IReadOnlyList<string> codes, CancellationToken cancellationToken);
    void ValidateOutput(string path);
}

public sealed class ExportService(IJournalStore store)
{
    public ExportRecord Export(ExportRequest request, IExportMedia media, CancellationToken cancellationToken = default)
    {
        request = request.Validate();
        if (File.Exists(request.TargetPath) || Directory.Exists(request.TargetPath))
            throw new IOException("This name is already in use. Choose another file name.");
        cancellationToken.ThrowIfCancellationRequested();
        var record = store.Reserve(request, media.SourceName, media.SourceType, media.CodeCount);
        var publishing = false;
        try
        {
            media.RenderTo(record.TemporaryPath, record.Codes, cancellationToken);
            media.ValidateOutput(record.TemporaryPath);
            var hash = FileChecksum(record.TemporaryPath);
            cancellationToken.ThrowIfCancellationRequested();
            // From this point cancellation must not separate publication from its journal entry.
            store.SetPublishing(record.Id, hash);
            publishing = true;
            File.Move(record.TemporaryPath, record.TargetPath, overwrite: false);
            store.SetReady(record.Id);
            return store.Find(record.Codes[0])!;
        }
        catch
        {
            // Publishing records remain searchable even if the final database update fails.
            if (!publishing)
            {
                try { File.Delete(record.TemporaryPath); }
                finally { store.MarkInterrupted(record.Id); }
            }
            throw;
        }
    }

    public static string FileChecksum(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
