namespace Context.Infrastructure.Abstractions;

/// <summary>
/// Runs work spanning several repositories in one database transaction. Every repository resolved in
/// the same scope shares one <see cref="ContextDbContext"/>, so their writes commit or roll back
/// together. A call made while a transaction is already open joins it instead of starting another.
/// </summary>
/// <remarks>
/// SQLite takes the write lock when the transaction begins, so concurrent units of work queue behind
/// each other. Nothing inside <paramref name="work"/> may open a second connection that writes (an
/// <c>IJobQueue.EnqueueAsync</c>, a vector index write): it would wait out the busy timeout on this
/// transaction's own lock. Insert jobs through <see cref="IJobRepository"/> instead.
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>True while a transaction is open on this scope's context. Code that must not do network I/O or
    /// wake other workers under the write lock checks this and leaves it to whoever commits.</summary>
    bool InTransaction { get; }

    Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct);

    async Task InTransactionAsync(Func<Task> work, CancellationToken ct) =>
        await InTransactionAsync(async () =>
        {
            await work().ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
}
