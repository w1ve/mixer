using System.Collections.Concurrent;

namespace MixerNet.Audio;

/// <summary>
/// Dedicated MTA thread that owns every Core Audio / NAudio object, so COM objects
/// are never shared across apartments with the WinForms STA UI thread.
/// </summary>
sealed class MtaWorker : IDisposable
{
    readonly BlockingCollection<Action> _queue = new();
    readonly Thread _thread;

    public MtaWorker()
    {
        _thread = new Thread(() =>
        {
            foreach (var job in _queue.GetConsumingEnumerable()) job();
        })
        {
            IsBackground = true,
            Name = "audio-mta",
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}
