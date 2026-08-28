using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class EmbeddingRepository(ContextDbContext db) : IEmbeddingRepository
{
    public Task<Embedding?> GetByContentHashAsync(string contentHash, CancellationToken ct) =>
        db.Embeddings.AsNoTracking().FirstOrDefaultAsync(e => e.ContentHash == contentHash, ct);

    public async Task<IReadOnlyDictionary<string, Embedding>> GetByContentHashesAsync(IEnumerable<string> contentHashes, CancellationToken ct)
    {
        var hashes = contentHashes.Distinct().ToArray();
        var rows = await db.Embeddings.AsNoTracking().Where(e => hashes.Contains(e.ContentHash)).ToListAsync(ct);
        return rows.ToDictionary(e => e.ContentHash);
    }

    public async Task<IReadOnlyList<string>> GetMissingContentHashesAsync(IEnumerable<string> contentHashes, CancellationToken ct)
    {
        var hashes = contentHashes.Distinct().ToArray();
        var present = await db.Embeddings.AsNoTracking()
            .Where(e => hashes.Contains(e.ContentHash))
            .Select(e => e.ContentHash)
            .ToListAsync(ct);

        var presentSet = present.ToHashSet();
        return hashes.Where(h => !presentSet.Contains(h)).ToList();
    }

    public async Task UpsertAsync(Embedding embedding, CancellationToken ct)
    {
        var existing = await db.Embeddings.FirstOrDefaultAsync(e => e.ContentHash == embedding.ContentHash, ct);
        if (existing is null)
        {
            db.Embeddings.Add(embedding);
        }
        else
        {
            existing.Provider = embedding.Provider;
            existing.Model = embedding.Model;
            existing.Dim = embedding.Dim;
            existing.Vector = embedding.Vector;
            existing.CreatedAt = embedding.CreatedAt;
        }

        await db.SaveChangesAsync(ct);
    }
}
