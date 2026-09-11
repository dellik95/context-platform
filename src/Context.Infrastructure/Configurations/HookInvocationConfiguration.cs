using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Context.Infrastructure.Configurations;

public class HookInvocationConfiguration : IEntityTypeConfiguration<HookInvocation>
{
    public void Configure(EntityTypeBuilder<HookInvocation> builder)
    {
        builder.ToTable("HookInvocations");
        builder.HasKey(h => h.Id);

        builder.Property(h => h.PayloadJson).IsRequired();
        builder.Property(h => h.Type).IsRequired();
        builder.Property(h => h.ReceivedAt).IsRequired();

        // No FK to Project - same unconstrained-Guid pattern as Job.ProjectId (JobConfiguration),
        // kept loose deliberately since this whole task started from a Restrict-FK deletion bug.
        builder.HasIndex(h => new { h.ProjectId, h.ReceivedAt });
    }
}
