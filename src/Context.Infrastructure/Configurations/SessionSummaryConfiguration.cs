using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class SessionSummaryConfiguration : IEntityTypeConfiguration<SessionSummary>
{
    public void Configure(EntityTypeBuilder<SessionSummary> builder)
    {
        builder.ToTable("SessionSummaries");
        builder.HasKey(s => s.SessionId);

        builder.Property(s => s.GeneratedAt).IsRequired();

        builder.HasOne<Session>()
            .WithOne()
            .HasForeignKey<SessionSummary>(s => s.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
