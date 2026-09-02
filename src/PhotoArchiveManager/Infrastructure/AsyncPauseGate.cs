namespace PhotoArchiveManager.Infrastructure;

public sealed class AsyncPauseGate
{
    private readonly object _sync = new();
    private TaskCompletionSource<bool> _resume = CompletedSource();
    private bool _paused;

    public bool IsPaused
    {
        get { lock (_sync) return _paused; }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_paused) return;
            _paused = true;
            _resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? source = null;
        lock (_sync)
        {
            if (!_paused) return;
            _paused = false;
            source = _resume;
        }
        source.TrySetResult(true);
    }

    public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task waitTask;
        lock (_sync)
            waitTask = _paused ? _resume.Task : Task.CompletedTask;
        await waitTask.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource<bool> CompletedSource()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(true);
        return tcs;
    }
}
