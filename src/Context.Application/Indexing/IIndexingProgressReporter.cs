namespace Context.Application.Indexing;

public interface IIndexingProgressReporter
{
    Task ReportAsync(ReindexProgressDto progress, CancellationToken ct);
}
