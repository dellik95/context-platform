using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Infrastructure.Configurations;

public class FileConfiguration : IEntityTypeConfiguration<DomainFile>
{
    public void Configure(EntityTypeBuilder<DomainFile> builder)
    {
        builder.ToTable("Files");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.RelativePath).IsRequired().HasMaxLength(1024);
        builder.Property(f => f.Language).HasMaxLength(64);
        builder.Property(f => f.ContentHash).IsRequired().HasMaxLength(64);
        builder.Property(f => f.UpdatedAt).IsRequired();

        builder.HasIndex(f => f.ProjectId);
        builder.HasIndex(f => new { f.ProjectId, f.RelativePath }).IsUnique();
        builder.HasIndex(f => f.ContentHash);

        builder.HasOne<Context.Domain.Entities.Project>()
            .WithMany()
            .HasForeignKey(f => f.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
