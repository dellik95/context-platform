using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Infrastructure.Configurations;

public class FileVersionConfiguration : IEntityTypeConfiguration<FileVersion>
{
    public void Configure(EntityTypeBuilder<FileVersion> builder)
    {
        builder.ToTable("FileVersions");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.ContentHash).IsRequired().HasMaxLength(64);
        builder.Property(v => v.IndexedAt).IsRequired();

        builder.HasIndex(v => v.FileId);

        builder.HasOne<DomainFile>()
            .WithMany()
            .HasForeignKey(v => v.FileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
