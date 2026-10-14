using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure;

public sealed class UnitOfWork(ContextDbContext db) : IUnitOfWork
{
    public bool InTransaction => db.Database.CurrentTransaction is not null;

    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return await work().ConfigureAwait(false);
        }

        // Microsoft.Data.Sqlite opens this as BEGIN IMMEDIATE: concurrent writers queue behind it.
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var result = await work().ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }
}
