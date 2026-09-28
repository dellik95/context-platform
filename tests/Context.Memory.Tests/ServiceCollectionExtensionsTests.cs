using Context.Memory;
using Context.Memory.Abstractions;
using Context.Memory.Consolidation;
using Context.Memory.Extraction;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Context.Memory.Tests;

/// <summary>
/// Explicit remember (<c>ContextEngine</c>) and extraction (<c>LlmMemoryExtractor</c>) must reach the
/// same <see cref="IMemoryConsolidator"/>: one scoped registration means one instance per scope.
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddContextMemory_registers_exactly_one_scoped_IMemoryConsolidator_backed_by_MemoryConsolidator()
    {
        var services = new TestServiceCollection();

        services.AddContextMemory();

        var consolidatorDescriptors = services.Where(d => d.ServiceType == typeof(IMemoryConsolidator)).ToList();

        consolidatorDescriptors.Should().ContainSingle(
            "both the memory_remember (ContextEngine) and hook/extraction (LlmMemoryExtractor) paths " +
            "must resolve IMemoryConsolidator from the same single registration");
        consolidatorDescriptors[0].Lifetime.Should().Be(ServiceLifetime.Scoped);
        consolidatorDescriptors[0].ImplementationType.Should().Be(typeof(MemoryConsolidator));
    }

    /// <summary>Minimal <see cref="IServiceCollection"/> so this test needs no dependency on the
    /// concrete <c>Microsoft.Extensions.DependencyInjection</c> container package - only the
    /// Abstractions package this project already references.</summary>
    private sealed class TestServiceCollection : List<ServiceDescriptor>, IServiceCollection
    {
    }
}
