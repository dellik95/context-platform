using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class MemorySymbolConfiguration : IEntityTypeConfiguration<MemorySymbol>
{
    public void Configure(EntityTypeBuilder<MemorySymbol> builder)
    {
        builder.ToTable("MemorySymbols");
        builder.HasKey(ms => new { ms.MemoryId, ms.SymbolId });

        builder.HasIndex(ms => ms.SymbolId);

        builder.HasOne<Memory>()
            .WithMany()
            .HasForeignKey(ms => ms.MemoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Symbol>()
            .WithMany()
            .HasForeignKey(ms => ms.SymbolId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
