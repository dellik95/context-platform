using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Infrastructure.Configurations;

public class ChunkConfiguration : IEntityTypeConfiguration<Chunk>
{
    public void Configure(EntityTypeBuilder<Chunk> builder)
    {
        builder.ToTable("Chunks");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Text).IsRequired();
        builder.Property(c => c.ContentHash).IsRequired().HasMaxLength(64);

        builder.HasIndex(c => c.FileId);
        builder.HasIndex(c => c.ContentHash);

        builder.HasOne<DomainFile>()
            .WithMany()
            .HasForeignKey(c => c.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Symbol>()
            .WithMany()
            .HasForeignKey(c => c.SymbolId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
