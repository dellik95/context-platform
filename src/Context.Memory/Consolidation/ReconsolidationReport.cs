using System.Text.Json;
using System.Text.Json.Serialization;
using Context.Domain.Enums;
using Context.Memory.Abstractions;

namespace Context.Memory.Consolidation;

/// <summary>
/// The plan of one batch reconsolidation run, stored as a report's JSON. Sources carry the Status and
/// LastReinforcedAt they had when planned, so applying later can tell a source that changed since.
/// </summary>
public sealed record ReconsolidationReport(
    IReadOnlyList<ReconsolidationGroup> Groups,
    IReadOnlyList<RetiredChangeLog> RetiredChangeLogs,
    int Untouched,
    ReconsolidationStats Stats)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Set once the report has been applied.</summary>
    public ReconsolidationApplyOutcome? Outcome { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ReconsolidationReport FromJson(string json) =>
        JsonSerializer.Deserialize<ReconsolidationReport>(json, JsonOptions)
            ?? throw new InvalidOperationException("Reconsolidation report JSON is empty.");
}

/// <param name="Index">1-based; what an apply's excluded groups refer to.</param>
/// <param name="KeepSeparate">Sources the model judged to be about another subject; left untouched.</param>
/// <param name="Ambiguous">The plan would lose a source fact or is self-inconsistent; never applied.</param>
public sealed record ReconsolidationGroup(
    int Index,
    IReadOnlyList<ReconsolidationSource> Sources,
    IReadOnlyList<ReconsolidationCanonical> Canonicals,
    IReadOnlyList<ReconsolidationContradiction> Contradictions,
    IReadOnlyList<Guid> KeepSeparate,
    bool Ambiguous,
    string? Note);

public sealed record ReconsolidationSource(
    Guid Id,
    string Title,
    MemoryType Type,
    MemoryStatus Status,
    DateTimeOffset LastReinforcedAt);

public sealed record ReconsolidationCanonical(
    MemoryType Type,
    string Title,
    string Body,
    string? Topic,
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> FilesRead,
    IReadOnlyList<string> FilesModified,
    IReadOnlyList<Guid> SourceIds);

public sealed record ReconsolidationContradiction(Guid A, Guid B, string? Reason, ContradictionCodeCheck? CodeCheck);

/// <param name="WinnerId">Set when the code confirms one side and refutes the other.</param>
public sealed record ContradictionCodeCheck(CodeCheckResult A, CodeCheckResult B, Guid? WinnerId, string? Note);

public sealed record RetiredChangeLog(Guid Id, string Title, MemoryStatus Status, DateTimeOffset LastReinforcedAt, string Reason);

public sealed record ReconsolidationStats(
    int LiveKnowledge,
    int Reembedded,
    int Groups,
    int AmbiguousGroups,
    int Canonicals,
    int SourcesMerged,
    int Contradictions,
    int RetiredChangeLogs,
    bool UsedModel);

public sealed record ReconsolidationApplyOutcome(
    DateTimeOffset AppliedAt,
    IReadOnlyList<int> AppliedGroups,
    IReadOnlyList<int> ExcludedGroups,
    IReadOnlyList<SkippedGroup> SkippedGroups,
    int RetiredChangeLogs,
    IReadOnlyList<Guid> SkippedChangeLogs);

public sealed record SkippedGroup(int Index, string Reason);

/// <param name="ResolveContradictions">Supersede the refuted side of a contradiction the code settles;
/// otherwise every contradiction only marks both sides Contested.</param>
public sealed record ReconsolidationApplyOptions(IReadOnlyCollection<int> ExcludeGroups, bool ResolveContradictions);
