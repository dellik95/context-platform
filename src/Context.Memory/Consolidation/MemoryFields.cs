using System.Text.Json;
using Context.Domain.Entities;

namespace Context.Memory.Consolidation;

/// <summary>Column-level helpers shared by online and batch consolidation.</summary>
internal static class MemoryFields
{
    public const int MaxNoteLength = 500;

    public static string? ToJsonOrNull(IReadOnlyCollection<string> values) =>
        values.Count > 0 ? JsonSerializer.Serialize(values) : null;

    public static string? UnionJson(string? json, IEnumerable<string> incoming) =>
        ToJsonOrNull(UnionList(EntityExtractor.ParseList(json), incoming));

    public static List<string> UnionList(IEnumerable<string> first, IEnumerable<string> second) =>
        [.. first.Concat(second).Distinct(StringComparer.OrdinalIgnoreCase)];

    public static List<MemoryEntity> UnionEntities(
        Context.Domain.Entities.Memory memory, IEnumerable<MemoryEntity> existing, IEnumerable<ExtractedEntity> incoming) =>
        [.. existing
            .Select(e => new ExtractedEntity(e.Key, e.Display))
            .Concat(incoming)
            .DistinctBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => new MemoryEntity { ProjectId = memory.ProjectId, MemoryId = memory.Id, Key = e.Key, Display = e.Display })];

    public static List<MemoryEntity> EntityRows(Context.Domain.Entities.Memory memory, IEnumerable<ExtractedEntity> entities) =>
        UnionEntities(memory, [], entities);

    public static string? Note(string? note) => note is { Length: > MaxNoteLength } ? note[..MaxNoteLength] : note;
}
