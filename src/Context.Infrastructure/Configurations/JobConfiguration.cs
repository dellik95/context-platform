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

        builder.HasIndex(j => new { j.Type, j.Status });
        builder.HasIndex(j => j.Status);
        builder.HasIndex(j => j.NextRetryAt);
        builder.HasIndex(j => new { j.ProjectId, j.Status });
    }
}
