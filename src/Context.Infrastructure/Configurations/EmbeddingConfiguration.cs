using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class EmbeddingConfiguration : IEntityTypeConfiguration<Embedding>
{
    public void Configure(EntityTypeBuilder<Embedding> builder)
    {
        builder.ToTable("Embeddings");

        // ContentHash is the primary key (CONTRACTS.md §1) — no separate identity FK is
        // maintained back from Chunk since embeddings are computed asynchronously and may
        // briefly lag chunk creation.
        builder.HasKey(e => e.ContentHash);

        builder.Property(e => e.ContentHash).HasMaxLength(64);
        builder.Property(e => e.Provider).IsRequired().HasMaxLength(128);
        builder.Property(e => e.Model).IsRequired().HasMaxLength(128);
        builder.Property(e => e.Vector).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
    }
}
