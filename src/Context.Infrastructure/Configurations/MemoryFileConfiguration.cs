using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Infrastructure.Configurations;

public class MemoryFileConfiguration : IEntityTypeConfiguration<MemoryFile>
{
    public void Configure(EntityTypeBuilder<MemoryFile> builder)
    {
        builder.ToTable("MemoryFiles");
        builder.HasKey(mf => new { mf.MemoryId, mf.FileId });

        builder.HasIndex(mf => mf.FileId);

        builder.HasOne<Memory>()
            .WithMany()
            .HasForeignKey(mf => mf.MemoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<DomainFile>()
            .WithMany()
            .HasForeignKey(mf => mf.FileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
