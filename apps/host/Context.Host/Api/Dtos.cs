using Context.Domain.Entities;

namespace Context.Host.Api;

public sealed record CreateProjectRequest(string Name, string RootPath, string? VcsRemote = null);

public sealed record ProjectDto(Guid Id, string Name, string RootPath, string? VcsRemote, DateTimeOffset CreatedAt)
{
    public static ProjectDto FromEntity(Project project) =>
        new(project.Id, project.Name, project.RootPath, project.VcsRemote, project.CreatedAt);
}

public sealed record ProjectStatusDto(
    ProjectDto Project,
    int FilesIndexed,
    int SymbolsIndexed,
    int MemoriesCount,
    DateTimeOffset? LastIndexedAt);

public sealed record IndexTriggerResponse(Guid ProjectId, bool Full, string Status);

public sealed record HealthResponse(
    bool Healthy,
    bool DatabaseReachable,
    bool JobDispatcherAlive,
    string EmbeddingProvider,
    string RerankerProvider,
    string LlmProvider);
