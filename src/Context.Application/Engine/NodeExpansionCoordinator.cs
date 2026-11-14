using Context.Domain.Entities;
using Context.Graph.Abstractions;
using Context.Infrastructure.Abstractions;
using Context.Shared;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Application.Engine;

/// <summary>
/// Handles progressive symbol/file expansion, graph blast-radius warm-up, and project summary stats
/// for <see cref="ContextEngine"/>.
/// </summary>
internal sealed class NodeExpansionCoordinator(
    IGraphTraversal graphTraversal,
    IGraphCacheProvider graphCacheProvider,
    IProjectRepository projectRepository,
    IFileRepository fileRepository,
    ISymbolRepository symbolRepository,
    IChunkRepository chunkRepository,
    IMemoryRepository memoryRepository)
{
    private const int SummaryCharLimit = 800;

    public async Task<object> ExpandAsync(Guid nodeId, DisclosureLevel level, CancellationToken ct)
    {
        var symbol = await symbolRepository.GetByIdAsync(nodeId, ct).ConfigureAwait(false);
        if (symbol is not null)
        {
            return await ExpandSymbolAsync(symbol, level, ct).ConfigureAwait(false);
        }

        var file = await fileRepository.GetByIdAsync(nodeId, ct).ConfigureAwait(false);
        if (file is not null)
        {
            return await ExpandFileAsync(file, level, ct).ConfigureAwait(false);
        }

        throw new KeyNotFoundException($"No symbol or file was found for node {nodeId}.");
    }

    public async Task<ImpactSet> GraphImpactAsync(Guid symbolId, int maxHops, CancellationToken ct)
    {
        var projectId = await ResolveProjectIdForNodeAsync(symbolId, ct).ConfigureAwait(false);
        if (projectId is { } id)
        {
            await graphCacheProvider.GetOrCreateAsync(id, ct).ConfigureAwait(false);
        }

        return await graphTraversal.BlastRadiusAsync(symbolId, maxHops, ct).ConfigureAwait(false);
    }

    public async Task<object> ProjectInfoAsync(Guid projectId, CancellationToken ct)
    {
        var project = await projectRepository.GetByIdAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Project {projectId} was not found.");

        var files = await fileRepository.ListByProjectAsync(projectId, includeDeleted: false, ct).ConfigureAwait(false);
        var symbolCount = await symbolRepository.CountByProjectAsync(projectId, ct).ConfigureAwait(false);
        DateTimeOffset? lastIndexedAt = files.Count > 0 ? files.Max(f => f.UpdatedAt) : null;
        var activeMemories = await memoryRepository.ListByProjectAsync(projectId, includeSuperseded: false, ct).ConfigureAwait(false);

        return new ProjectInfo(
            project.Id,
            project.Name,
            project.RootPath,
            FilesIndexed: files.Count,
            SymbolsIndexed: symbolCount,
            MemoriesCount: activeMemories.Count,
            LastIndexedAt: lastIndexedAt);
    }

    private async Task<Guid?> ResolveProjectIdForNodeAsync(Guid nodeId, CancellationToken ct)
    {
        var symbol = await symbolRepository.GetByIdAsync(nodeId, ct).ConfigureAwait(false);
        if (symbol is not null)
        {
            var owningFile = await fileRepository.GetByIdAsync(symbol.FileId, ct).ConfigureAwait(false);
            if (owningFile is not null)
            {
                return owningFile.ProjectId;
            }
        }

        var file = await fileRepository.GetByIdAsync(nodeId, ct).ConfigureAwait(false);
        return file?.ProjectId;
    }

    private async Task<object> ExpandSymbolAsync(Symbol symbol, DisclosureLevel level, CancellationToken ct)
    {
        var file = await fileRepository.GetByIdAsync(symbol.FileId, ct).ConfigureAwait(false);

        if (level == DisclosureLevel.L0_Metadata)
        {
            return new ExpandedNode(symbol.Id, "Symbol", symbol.QualifiedName, file?.RelativePath, symbol.StartLine, symbol.EndLine, DocComment: null, Content: null);
        }

        string? content = null;
        if (file is not null)
        {
            var project = await projectRepository.GetByIdAsync(file.ProjectId, ct).ConfigureAwait(false);
            if (project is not null)
            {
                content = await TryReadSourceRangeAsync(project.RootPath, file.RelativePath, symbol.StartLine, symbol.EndLine, ct).ConfigureAwait(false);
            }
        }

        content ??= await FindChunkTextForSymbolAsync(symbol, ct).ConfigureAwait(false);

        return new ExpandedNode(
            symbol.Id, "Symbol", symbol.QualifiedName, file?.RelativePath, symbol.StartLine, symbol.EndLine,
            symbol.DocComment,
            level == DisclosureLevel.L1_Summary ? Truncate(content, SummaryCharLimit) : content);
    }

    private async Task<object> ExpandFileAsync(DomainFile file, DisclosureLevel level, CancellationToken ct)
    {
        if (level == DisclosureLevel.L0_Metadata)
        {
            return new ExpandedNode(file.Id, "File", file.RelativePath, file.RelativePath, StartLine: null, EndLine: null, DocComment: null, Content: null);
        }

        string? content = null;
        var project = await projectRepository.GetByIdAsync(file.ProjectId, ct).ConfigureAwait(false);
        if (project is not null)
        {
            var absolutePath = Path.Combine(project.RootPath, file.RelativePath);
            if (System.IO.File.Exists(absolutePath))
            {
                content = await System.IO.File.ReadAllTextAsync(absolutePath, ct).ConfigureAwait(false);
            }
        }

        return new ExpandedNode(
            file.Id, "File", file.RelativePath, file.RelativePath, StartLine: null, EndLine: null, DocComment: null,
            Content: level == DisclosureLevel.L1_Summary ? Truncate(content, SummaryCharLimit) : content);
    }

    private async Task<string?> FindChunkTextForSymbolAsync(Symbol symbol, CancellationToken ct)
    {
        var chunks = await chunkRepository.ListByFileAsync(symbol.FileId, ct).ConfigureAwait(false);
        return chunks.FirstOrDefault(c => c.SymbolId == symbol.Id)?.Text;
    }

    private static async Task<string?> TryReadSourceRangeAsync(string rootPath, string relativePath, int startLine, int endLine, CancellationToken ct)
    {
        var absolutePath = Path.Combine(rootPath, relativePath);
        if (!System.IO.File.Exists(absolutePath))
        {
            return null;
        }

        var lines = await System.IO.File.ReadAllLinesAsync(absolutePath, ct).ConfigureAwait(false);
        if (startLine <= 0 || endLine < startLine || startLine > lines.Length)
        {
            return null;
        }

        var from = startLine - 1;
        var to = Math.Min(lines.Length, endLine);
        return string.Join(Environment.NewLine, lines[from..to]);
    }

    private static string? Truncate(string? text, int maxChars) =>
        text is null || text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, maxChars), "…");
}
