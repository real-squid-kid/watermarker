namespace WaterMarker.Core;

public interface IRecoveryStore
{
    IReadOnlyList<ExportRecord> GetIncomplete();
    void SetReady(Guid id);
    void MarkInterrupted(Guid id);
}

public sealed record RecoveryResult(int Completed, int Interrupted, int Pending);

public sealed class ExportRecovery(IRecoveryStore store)
{
    public RecoveryResult Recover()
    {
        var completed = 0;
        var interrupted = 0;
        var pending = 0;
        foreach (var record in store.GetIncomplete())
        {
            try
            {
                // A database path is not sufficient proof of ownership: recompute it from the operation ID.
                if (!ExportPaths.OwnsTemporary(record))
                {
                    store.MarkInterrupted(record.Id);
                    interrupted++;
                    continue;
                }
                var canPublish = record.State == ExportState.Publishing && record.Checksum is not null;
                if (canPublish && File.Exists(record.TargetPath) && ExportService.FileChecksum(record.TargetPath) == record.Checksum)
                {
                    File.Delete(record.TemporaryPath);
                    store.SetReady(record.Id);
                    completed++;
                    continue;
                }
                if (canPublish && !File.Exists(record.TargetPath) && !Directory.Exists(record.TargetPath) &&
                    File.Exists(record.TemporaryPath) && ExportService.FileChecksum(record.TemporaryPath) == record.Checksum)
                {
                    try { File.Move(record.TemporaryPath, record.TargetPath, overwrite: false); }
                    catch (IOException) when (File.Exists(record.TargetPath) || Directory.Exists(record.TargetPath))
                    {
                        // Another process may occupy the name between the check and rename.
                        if (!File.Exists(record.TargetPath) || ExportService.FileChecksum(record.TargetPath) != record.Checksum)
                        {
                            File.Delete(record.TemporaryPath);
                            store.MarkInterrupted(record.Id);
                            interrupted++;
                            continue;
                        }
                        File.Delete(record.TemporaryPath);
                    }
                    store.SetReady(record.Id);
                    completed++;
                    continue;
                }
                File.Delete(record.TemporaryPath);
                store.MarkInterrupted(record.Id);
                interrupted++;
            }
            catch (IOException) { pending++; }
            catch (UnauthorizedAccessException) { pending++; }
        }
        return new(completed, interrupted, pending);
    }
}
