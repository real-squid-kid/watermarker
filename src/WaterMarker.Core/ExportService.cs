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
    IReadOnlyList<ExportRecord> SearchCodes(string fragment);
}

public interface IExportMedia
{
    string SourceName { get; }
    string SourceType { get; }
    int CodeCount { get; }
    void RenderTo(string path, IReadOnlyList<string> codes, CancellationToken cancellationToken);
    void ValidateOutput(string path);
    void ValidateOutput(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOutput(path);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

public sealed class ExportService(IJournalStore store, Action<ExportCheckpoint, ExportRecord>? checkpoint = null)
{
    public ExportRecord Export(ExportRequest request, IExportMedia media, CancellationToken cancellationToken = default, ExportControl? control = null)
    {
        using var localControl = control is null ? new ExportControl() : null;
        control ??= localControl!;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, control.Token);
        var token = linked.Token;
        request = request.Validate();
        if (File.Exists(request.TargetPath) || Directory.Exists(request.TargetPath))
            throw new IOException("This name is already in use. Choose another file name.");
        token.ThrowIfCancellationRequested();
        var record = store.Reserve(request, media.SourceName, media.SourceType, media.CodeCount);
        var publishing = false;
        try
        {
            checkpoint?.Invoke(ExportCheckpoint.Reserved, record);
            media.RenderTo(record.TemporaryPath, record.Codes, token);
            checkpoint?.Invoke(ExportCheckpoint.Rendered, record);
            media.ValidateOutput(record.TemporaryPath, token);
            var hash = FileChecksumCancellable(record.TemporaryPath, token);
            control.BeginPublishing(token);
            checkpoint?.Invoke(ExportCheckpoint.PublicationStarting, record);
            // From this point cancellation must not separate publication from its journal entry.
            store.SetPublishing(record.Id, hash);
            publishing = true;
            checkpoint?.Invoke(ExportCheckpoint.Publishing, record);
            File.Move(record.TemporaryPath, record.TargetPath, overwrite: false);
            checkpoint?.Invoke(ExportCheckpoint.Published, record);
            store.SetReady(record.Id);
            checkpoint?.Invoke(ExportCheckpoint.Ready, record);
            return store.Find(record.Codes[0])!;
        }
        catch
        {
            // Publishing records remain searchable even if the final database update fails.
            if (!publishing)
            {
                try
                {
                    if (ExportPaths.OwnsTemporary(record)) File.Delete(record.TemporaryPath);
                    store.MarkInterrupted(record.Id);
                }
                // Preserve the original failure; the persisted Rendering record makes cleanup retryable.
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or System.Data.Common.DbException) { }
            }
            throw;
        }
    }

    public static string FileChecksum(string path) => FileChecksumCancellable(path, CancellationToken.None);

    private static string FileChecksumCancellable(string path, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
