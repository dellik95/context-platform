using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("Sessions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.ClaudeSessionId).HasMaxLength(128);
        builder.Property(s => s.StartedAt).IsRequired();
        builder.Property(s => s.Cwd).HasMaxLength(1024);
        builder.Property(s => s.EndReason).HasMaxLength(128);

        builder.HasIndex(s => s.ProjectId);
        builder.HasIndex(s => s.ClaudeSessionId);

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(s => s.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
