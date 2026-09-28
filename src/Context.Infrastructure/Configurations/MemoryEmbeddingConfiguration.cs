using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class MemoryEmbeddingConfiguration : IEntityTypeConfiguration<MemoryEmbedding>
{
    public void Configure(EntityTypeBuilder<MemoryEmbedding> builder)
    {
        builder.ToTable("MemoryEmbeddings");
        builder.HasKey(e => e.MemoryId);

        builder.Property(e => e.Vector).IsRequired();
        builder.Property(e => e.ModelKey).HasMaxLength(256);

        builder.HasOne<Memory>()
            .WithOne()
            .HasForeignKey<MemoryEmbedding>(e => e.MemoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
