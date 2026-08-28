using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class ClaudeIntegrationConfiguration : IEntityTypeConfiguration<ClaudeIntegration>
{
    public void Configure(EntityTypeBuilder<ClaudeIntegration> builder)
    {
        builder.ToTable("ClaudeIntegrations");
        builder.HasKey(c => c.ProjectId);

        builder.Property(c => c.HookVersion).IsRequired().HasMaxLength(64);
        builder.Property(c => c.McpConfigHash).IsRequired().HasMaxLength(128);

        builder.HasOne<Project>()
            .WithOne()
            .HasForeignKey<ClaudeIntegration>(c => c.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
