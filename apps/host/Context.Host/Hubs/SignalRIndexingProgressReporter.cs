using Context.Application.Indexing;

namespace Context.Host.Hubs;

/// <summary>
/// SignalR-backed <see cref="IIndexingProgressReporter"/> (CONTRACTS.md §12) — registered in
/// Program.cs after <c>AddContextApplication</c> so it overrides the default
/// <see cref="NullIndexingProgressReporter"/> registration.
/// </summary>
public sealed class SignalRIndexingProgressReporter(IndexingHubNotifier hubNotifier) : IIndexingProgressReporter
{
    public Task ReportAsync(ReindexProgressDto progress, CancellationToken ct) =>
        hubNotifier.ReindexProgressAsync(progress.ProjectId, progress, ct);
}
