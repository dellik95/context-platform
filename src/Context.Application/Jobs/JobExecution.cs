namespace Context.Application.Jobs;

/// <summary>
/// Tracks one in-process run of a claimed <see cref="Domain.Entities.Job"/>;
/// <see cref="Supersede"/> lets <see cref="JobDispatcher.ForceRunAsync"/> cancel an active execution
/// and stop it from overwriting the job row after a forced claim takes over.
/// </summary>
internal sealed class JobExecution(CancellationToken lifetime) : IDisposable
{
    private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _superseded;

    public CancellationToken Token => _cts.Token;
    public bool Superseded => _superseded;
    public Task Completion => _completion.Task;

    public void Supersede()
    {
        _superseded = true;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Finished between being looked up and being cancelled.
        }
    }

    public void Dispose()
    {
        _completion.TrySetResult();
        _cts.Dispose();
    }
}
