using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class EmbeddingRepository(ContextDbContext db) : IEmbeddingRepository
{
    public Task<Embedding?> GetByContentHashAsync(string contentHash, string provider, CancellationToken ct) =>
        db.Embeddings.AsNoTracking().FirstOrDefaultAsync(e => e.ContentHash == contentHash && e.Provider == provider, ct);

    public async Task<IReadOnlyDictionary<string, Embedding>> GetByContentHashesAsync(IEnumerable<string> contentHashes, string provider, CancellationToken ct)
    {
        var hashes = contentHashes.Distinct().ToArray();
        var rows = await db.Embeddings.AsNoTracking()
            .Where(e => e.Provider == provider && hashes.Contains(e.ContentHash))
            .ToListAsync(ct);
        return rows.ToDictionary(e => e.ContentHash);
    }

    public async Task<IReadOnlyList<string>> GetMissingContentHashesAsync(IEnumerable<string> contentHashes, string provider, CancellationToken ct)
    {
        var hashes = contentHashes.Distinct().ToArray();
        var present = await db.Embeddings.AsNoTracking()
            .Where(e => e.Provider == provider && hashes.Contains(e.ContentHash))
            .Select(e => e.ContentHash)
            .ToListAsync(ct);

        var presentSet = present.ToHashSet();
        return hashes.Where(h => !presentSet.Contains(h)).ToList();
    }

    public async Task UpsertAsync(Embedding embedding, CancellationToken ct)
    {
        // Match on the full (ContentHash, Provider) key -- with Provider now part of the key, a
        // ContentHash-only match could hit a different provider's row and either corrupt it or
        // miss an existing row entirely, throwing on insert.
        var existing = await db.Embeddings.FirstOrDefaultAsync(
            e => e.ContentHash == embedding.ContentHash && e.Provider == embedding.Provider, ct);
        if (existing is null)
        {
            db.Embeddings.Add(embedding);
        }
        else
        {
            existing.Model = embedding.Model;
            existing.Dim = embedding.Dim;
            existing.Vector = embedding.Vector;
            existing.CreatedAt = embedding.CreatedAt;
        }

        await db.SaveChangesAsync(ct);
    }
}
