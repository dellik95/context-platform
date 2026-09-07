using Context.Memory;
using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Context.Memory.Tests;

/// <summary>
/// Covers MEMORY_PIPELINE_PLAN.md §H test #12: MCP's explicit-remember path
/// (<c>ContextEngine.RememberExplicitlyAsync</c>) and the hook-driven extraction path
/// (<c>LlmMemoryExtractor</c>) must reach the same <see cref="IMemoryConsolidator"/> - not two
/// parallel consolidation implementations. Verified structurally at the DI-registration level
/// (no full integration test needed): <see cref="ServiceCollectionExtensions.AddContextMemory"/>
/// registers exactly one <see cref="IMemoryConsolidator"/> service, Scoped, backed by
/// <see cref="MemoryConsolidator"/> - so any two consumers resolved from the same DI scope (as
/// both <c>ContextEngine</c> and <c>LlmMemoryExtractor</c> are, within one request) necessarily
/// get the identical instance.
/// </summary>
public class ServiceCollectionExtensionsTests
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
