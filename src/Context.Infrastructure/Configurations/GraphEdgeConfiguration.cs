using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Infrastructure.Configurations;

public class GraphEdgeConfiguration : IEntityTypeConfiguration<GraphEdge>
{
    public void Configure(EntityTypeBuilder<GraphEdge> builder)
    {
        builder.ToTable("GraphEdges");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Weight).IsRequired().HasDefaultValue(1.0);

        builder.HasIndex(e => e.ProjectId);
        builder.HasIndex(e => e.OwningFileId);
        builder.HasIndex(e => new { e.SourceNodeId, e.Kind });
        builder.HasIndex(e => new { e.TargetNodeId, e.Kind });

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<GraphNode>()
            .WithMany()
            .HasForeignKey(e => e.SourceNodeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<GraphNode>()
            .WithMany()
            .HasForeignKey(e => e.TargetNodeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<DomainFile>()
            .WithMany()
            .HasForeignKey(e => e.OwningFileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
