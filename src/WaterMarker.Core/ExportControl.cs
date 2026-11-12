namespace WaterMarker.Core;

public enum ExportCheckpoint { Reserved, Rendered, PublicationStarting, Publishing, Published, Ready }

public sealed class ExportControl : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private bool publishing;
    public CancellationToken Token => cancellation.Token;
    public bool CanCancel { get { lock (gate) return !publishing && !cancellation.IsCancellationRequested; } }

    public bool TryCancel()
    {
        lock (gate)
        {
            if (publishing || cancellation.IsCancellationRequested) return false;
            cancellation.Cancel();
            return true;
        }
    }

    public void BeginPublishing(CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            cancellation.Token.ThrowIfCancellationRequested();
            publishing = true;
        }
    }

    public void Dispose() => cancellation.Dispose();
}
