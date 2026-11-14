using Context.Graph.Abstractions;
using Context.Infrastructure.Abstractions;

namespace Context.Dashboard.Server.Mcp;

/// <summary>
/// Resolves symbol/file node ids to their owning indexed <see cref="Context.Domain.Entities.File"/>
/// and warms per-project in-memory graph caches before node-keyed graph traversals.
/// </summary>
public sealed class McpNodeResolver(
    ISymbolRepository symbolRepository,
    IFileRepository fileRepository,
    IGraphCacheProvider graphCacheProvider)
{
    public async Task WarmGraphCacheForNodeAsync(Guid nodeId, CancellationToken ct)
    {
        var file = await ResolveFileForNodeAsync(nodeId, ct).ConfigureAwait(false);
        if (file is not null)
        {
            await graphCacheProvider.GetOrCreateAsync(file.ProjectId, ct).ConfigureAwait(false);
        }
    }

    public async Task<Context.Domain.Entities.File?> ResolveFileForNodeAsync(Guid nodeId, CancellationToken ct)
    {
        var symbol = await symbolRepository.GetByIdAsync(nodeId, ct).ConfigureAwait(false);
        if (symbol is not null)
        {
            var owningFile = await fileRepository.GetByIdAsync(symbol.FileId, ct).ConfigureAwait(false);
            if (owningFile is not null)
            {
                return owningFile;
            }
        }

        return await fileRepository.GetByIdAsync(nodeId, ct).ConfigureAwait(false);
    }
}
