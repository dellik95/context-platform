using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class SessionSummaryChunkConfiguration : IEntityTypeConfiguration<SessionSummaryChunk>
{
    public void Configure(EntityTypeBuilder<SessionSummaryChunk> builder)
    {
        builder.ToTable("SessionSummaryChunks");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.FromEventAt).IsRequired();
        builder.Property(c => c.ToEventAt).IsRequired();
        builder.Property(c => c.GeneratedAt).IsRequired();

        builder.HasIndex(c => new { c.SessionId, c.SequenceNumber }).IsUnique();

        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(c => c.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
