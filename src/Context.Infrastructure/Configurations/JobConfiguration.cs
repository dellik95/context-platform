using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("Jobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.PayloadJson).IsRequired();
        builder.Property(j => j.Status).IsRequired();
        builder.Property(j => j.CreatedAt).IsRequired();

        // NOTE: CONTRACTS.md's Job entity (§1) has no ProjectId column, so the
        // "(ProjectId,Type)" index called for in the task brief is not applicable here.
        // Indexing on (Type,Status) and NextRetryAt instead, since that is what the
        // worker's dequeue-next-runnable-job query actually needs.
        builder.HasIndex(j => new { j.Type, j.Status });
        builder.HasIndex(j => j.Status);
        builder.HasIndex(j => j.NextRetryAt);
    }
}
