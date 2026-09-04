using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("Projects");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name).IsRequired().HasMaxLength(256);
        builder.Property(p => p.RootPath).IsRequired().HasMaxLength(1024);
        builder.Property(p => p.VcsRemote).HasMaxLength(512);
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.BearerTokenHash).HasMaxLength(64);

        builder.HasIndex(p => p.RootPath).IsUnique();
        builder.HasIndex(p => p.BearerTokenHash).IsUnique();
    }
}
