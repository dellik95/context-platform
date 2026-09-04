namespace Context.Application.Indexing;

/// <summary>
/// DI default for <see cref="IIndexingProgressReporter"/> so Context.Application has zero hard
/// dependency on SignalR (Context.Dashboard.Server overrides this registration with a SignalR-backed one —
/// see CONTRACTS.md §12). Mirrors <c>Microsoft.Extensions.Logging.Abstractions.NullLogger&lt;T&gt;</c>'s
/// own private-constructor/static-Instance shape.
/// </summary>
public sealed class NullIndexingProgressReporter : IIndexingProgressReporter
{
    public static readonly NullIndexingProgressReporter Instance = new();

    private NullIndexingProgressReporter()
    {
    }

    public Task ReportAsync(ReindexProgressDto progress, CancellationToken ct) => Task.CompletedTask;
}
