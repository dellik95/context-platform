using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class EmbeddingConfiguration : IEntityTypeConfiguration<Embedding>
{
    public void Configure(EntityTypeBuilder<Embedding> builder)
    {
        builder.ToTable("Embeddings");

        // (ContentHash, Provider) is the composite primary key -- a bare ContentHash key let
        // switching providers (e.g. noop -> ollama) silently reuse another provider's stale
        // vector for the same content, since one hash could only ever own one row. No separate
        // identity FK is maintained back from Chunk since embeddings are computed asynchronously
        // and may briefly lag chunk creation.
        builder.HasKey(e => new { e.ContentHash, e.Provider });

        builder.Property(e => e.ContentHash).HasMaxLength(64);
        builder.Property(e => e.Provider).IsRequired().HasMaxLength(128);
        builder.Property(e => e.Model).IsRequired().HasMaxLength(128);
        builder.Property(e => e.Vector).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
    }
}
