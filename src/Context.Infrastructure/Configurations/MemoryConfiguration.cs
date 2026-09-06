using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class MemoryConfiguration : IEntityTypeConfiguration<Memory>
{
    public void Configure(EntityTypeBuilder<Memory> builder)
    {
        builder.ToTable("Memories");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Title).IsRequired().HasMaxLength(512);
        builder.Property(m => m.Body).IsRequired();
        builder.Property(m => m.Topic).HasMaxLength(100);
        builder.Property(m => m.Confidence).IsRequired().HasDefaultValue(0.7);
        builder.Property(m => m.Tier).IsRequired();
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Property(m => m.LastReinforcedAt).IsRequired();

        builder.HasIndex(m => new { m.ProjectId, m.Type });
        builder.HasIndex(m => new { m.ProjectId, m.Topic });
        builder.HasIndex(m => m.SupersededById);

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(m => m.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // Self-referencing supersession link: Restrict to avoid cascade cycles.
        builder.HasOne<Memory>()
            .WithMany()
            .HasForeignKey(m => m.SupersededById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(m => m.CreatedFromSessionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
