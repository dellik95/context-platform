using Microsoft.Extensions.AI;

namespace Context.Providers.Abstractions;

/// <summary>
/// Resolves the <see cref="IEmbeddingGenerator{String, Embedding}"/>/<see cref="IChatClient"/> a
/// specific project should use, per that project's own saved settings — replaces the old model of
/// one process-wide singleton pair chosen once at host startup (see
/// <c>ServiceCollectionExtensions.AddContextProviders</c>, still used only by the single-project
/// default/<c>Context.Cli</c> path). Implemented by <c>Context.Application.Providers.
/// ProjectProviderResolver</c>, which reads <c>IProjectSettingsService.GetAsync(projectId, ct)</c>
/// fresh on every call — this is what lets a Settings-tab save take effect immediately, no host
/// restart needed.
/// </summary>
public interface IProviderResolver
{
    Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(Guid projectId, CancellationToken ct);

    Task<IChatClient> GetChatClientAsync(Guid projectId, CancellationToken ct);
}
