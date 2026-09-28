using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class MemoryEvidenceConfiguration : IEntityTypeConfiguration<MemoryEvidence>
{
    public void Configure(EntityTypeBuilder<MemoryEvidence> builder)
    {
        builder.ToTable("MemoryEvidence");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Relation).IsRequired();
        builder.Property(e => e.Source).IsRequired();
        builder.Property(e => e.ObservedAt).IsRequired();
        builder.Property(e => e.Note).HasMaxLength(2000);

        builder.HasIndex(e => e.MemoryId);
        builder.HasIndex(e => new { e.ProjectId, e.SourceMemoryId });

        // SessionId/SourceMemoryId are deliberately FK-less: evidence outlives the rows it cites.
        builder.HasOne<Memory>()
            .WithMany()
            .HasForeignKey(e => e.MemoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
