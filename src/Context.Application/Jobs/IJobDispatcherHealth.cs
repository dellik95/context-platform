namespace Context.Application.Jobs;

/// <summary>
/// Read-only liveness contract for the background job dispatcher, exposed to health endpoints so
/// <see cref="IJobQueue"/> stays focused solely on enqueueing and job control.
/// </summary>
public interface IJobDispatcherHealth
{
    bool IsRunning { get; }
}
