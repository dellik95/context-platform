using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class SymbolRelationConfiguration : IEntityTypeConfiguration<SymbolRelation>
{
    public void Configure(EntityTypeBuilder<SymbolRelation> builder)
    {
        builder.ToTable("SymbolRelations");
        builder.HasKey(r => r.Id);

        builder.HasIndex(r => r.SourceSymbolId);
        builder.HasIndex(r => r.TargetSymbolId);
        builder.HasIndex(r => new { r.SourceSymbolId, r.Kind });

        builder.HasOne<Symbol>()
            .WithMany()
            .HasForeignKey(r => r.SourceSymbolId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Symbol>()
            .WithMany()
            .HasForeignKey(r => r.TargetSymbolId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
