using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Infrastructure.Configurations;

public class SymbolConfiguration : IEntityTypeConfiguration<Symbol>
{
    public void Configure(EntityTypeBuilder<Symbol> builder)
    {
        builder.ToTable("Symbols");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Name).IsRequired().HasMaxLength(512);
        builder.Property(s => s.QualifiedName).IsRequired().HasMaxLength(1024);
        builder.Property(s => s.SignatureHash).IsRequired().HasMaxLength(64);
        builder.Property(s => s.BodyHash).IsRequired().HasMaxLength(64);

        builder.HasIndex(s => s.FileId);
        builder.HasIndex(s => s.QualifiedName);
        builder.HasIndex(s => s.ParentSymbolId);

        builder.HasOne<DomainFile>()
            .WithMany()
            .HasForeignKey(s => s.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        // Self-referencing parent link: Restrict to avoid cascade cycles.
        builder.HasOne<Symbol>()
            .WithMany()
            .HasForeignKey(s => s.ParentSymbolId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
