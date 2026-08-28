using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Infrastructure.Configurations;

public class GraphNodeConfiguration : IEntityTypeConfiguration<GraphNode>
{
    public void Configure(EntityTypeBuilder<GraphNode> builder)
    {
        builder.ToTable("GraphNodes");
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Label).IsRequired().HasMaxLength(1024);

        builder.HasIndex(n => n.ProjectId);
        builder.HasIndex(n => n.OwningFileId);
        builder.HasIndex(n => n.RefId);

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(n => n.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<DomainFile>()
            .WithMany()
            .HasForeignKey(n => n.OwningFileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
