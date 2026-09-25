using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class SessionEventConfiguration : IEntityTypeConfiguration<SessionEvent>
{
    public void Configure(EntityTypeBuilder<SessionEvent> builder)
    {
        builder.ToTable("SessionEvents");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.PayloadJson).IsRequired();
        builder.Property(e => e.Timestamp).IsRequired();

        builder.HasIndex(e => e.SessionId);
        builder.HasIndex(e => new { e.SessionId, e.Type });
        builder.HasIndex(e => new { e.SessionId, e.ExtractionBatchId });

        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(e => e.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
