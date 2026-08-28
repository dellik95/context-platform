using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class MemoryRelationConfiguration : IEntityTypeConfiguration<MemoryRelation>
{
    public void Configure(EntityTypeBuilder<MemoryRelation> builder)
    {
        builder.ToTable("MemoryRelations");
        builder.HasKey(r => r.Id);

        builder.HasIndex(r => r.MemoryId);
        builder.HasIndex(r => r.RelatedMemoryId);

        builder.HasOne<Memory>()
            .WithMany()
            .HasForeignKey(r => r.MemoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Memory>()
            .WithMany()
            .HasForeignKey(r => r.RelatedMemoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
