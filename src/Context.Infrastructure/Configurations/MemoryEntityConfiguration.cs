using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class MemoryEntityConfiguration : IEntityTypeConfiguration<MemoryEntity>
{
    public void Configure(EntityTypeBuilder<MemoryEntity> builder)
    {
        builder.ToTable("MemoryEntities");
        builder.HasKey(e => new { e.MemoryId, e.Key });

        builder.Property(e => e.Key).IsRequired().HasMaxLength(512);
        builder.Property(e => e.Display).IsRequired().HasMaxLength(512);

        builder.HasIndex(e => new { e.ProjectId, e.Key });

        builder.HasOne<Memory>()
            .WithMany()
            .HasForeignKey(e => e.MemoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
