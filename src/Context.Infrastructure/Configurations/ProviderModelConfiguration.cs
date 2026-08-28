using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class ProviderModelConfiguration : IEntityTypeConfiguration<ProviderModel>
{
    public void Configure(EntityTypeBuilder<ProviderModel> builder)
    {
        builder.ToTable("ProviderModels");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.ModelName).IsRequired().HasMaxLength(256);

        builder.HasIndex(m => m.ProviderId);

        builder.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(m => m.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
