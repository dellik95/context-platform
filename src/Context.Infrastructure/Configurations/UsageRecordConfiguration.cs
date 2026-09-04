using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class UsageRecordConfiguration : IEntityTypeConfiguration<UsageRecord>
{
    public void Configure(EntityTypeBuilder<UsageRecord> builder)
    {
        builder.ToTable("UsageRecords");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Purpose).IsRequired();
        builder.Property(u => u.Provider).IsRequired();
        builder.Property(u => u.CreatedAt).IsRequired();

        builder.HasIndex(u => new { u.ProjectId, u.CreatedAt });
        builder.HasIndex(u => u.SessionId);
    }
}
