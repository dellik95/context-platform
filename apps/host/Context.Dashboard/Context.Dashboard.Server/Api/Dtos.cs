using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Consolidation;

namespace Context.Dashboard.Server.Api;

public sealed record CreateProjectRequest(string Name, string RootPath, string? VcsRemote = null);

/// <summary>
/// <paramref name="Token"/> is populated only in <c>ProjectEndpoints.CreateAsync</c>'s response —
/// the raw bearer token can never be recovered from <c>Project.BearerTokenHash</c> once minted, so
/// that creation response is the only place this is ever non-null. <see cref="FromEntity"/> (used
/// everywhere else this DTO is returned — list, status) never sets it.
/// </summary>
public sealed record ProjectDto(Guid Id, string Name, string RootPath, string? VcsRemote, DateTimeOffset CreatedAt, string? Token = null)
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

public sealed record RecreateEmbeddingsTriggerResponse(Guid ProjectId, Guid JobId, string Status);

public sealed record HealthResponse(
    bool Healthy,
    bool DatabaseReachable,
    bool JobDispatcherAlive,
    string EmbeddingProvider,
    string RerankerProvider,
    string LlmProvider);

public sealed record ReconsolidationTriggerResponse(Guid ProjectId, Guid JobId, bool DryRun, Guid? ReportId, string Status);

public sealed record ApplyReconsolidationRequest(IReadOnlyList<int>? ExcludeGroups);

public sealed record ReconsolidationReportSummaryDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    ConsolidationReportStatus Status,
    DateTimeOffset? AppliedAt,
    ReconsolidationStats? Stats);

public sealed record ReconsolidationReportDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    ConsolidationReportStatus Status,
    DateTimeOffset? AppliedAt,
    ReconsolidationReport Report);
