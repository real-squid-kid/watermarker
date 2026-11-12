using System.Windows.Threading;

namespace WaterMarker.Rendering;

public static class StaWorker
{
    public static Task<T> Run<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(action()); }
            catch (OperationCanceledException ex) { completion.SetCanceled(ex.CancellationToken); }
            catch (Exception ex) { completion.SetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true, Name = "WaterMarker media" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
