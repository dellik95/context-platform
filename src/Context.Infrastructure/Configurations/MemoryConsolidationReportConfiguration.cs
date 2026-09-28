using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class MemoryConsolidationReportConfiguration : IEntityTypeConfiguration<MemoryConsolidationReport>
{
    public void Configure(EntityTypeBuilder<MemoryConsolidationReport> builder)
    {
        builder.ToTable("MemoryConsolidationReports");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.Status).IsRequired();
        builder.Property(r => r.ReportJson).IsRequired();

        builder.HasIndex(r => new { r.ProjectId, r.Status });

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(r => r.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
