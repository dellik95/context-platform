using System.Globalization;
using Context.Domain.Enums;
using Context.Memory.Consolidation;

namespace Context.Memory.Search;

/// <summary>
/// Canonical text rendering of a memory for packs and hooks:
/// <c>[Type · Topic · conf 0.00 · N sources · CONTESTED] Title: Body</c>.
/// </summary>
public static class MemoryNote
{
    private const string Separator = " · ";

    public static string Header(Context.Domain.Entities.Memory memory)
    {
        var parts = new List<string>(5) { memory.Type.ToString() };
        if (!string.IsNullOrWhiteSpace(memory.Topic))
        {
            parts.Add(memory.Topic.Trim());
        }

        parts.Add("conf " + memory.Confidence.ToString("0.00", CultureInfo.InvariantCulture));
        parts.Add(memory.SourceCount == 1 ? "1 source" : $"{memory.SourceCount} sources");
        if (memory.Status == MemoryStatus.Contested)
        {
            parts.Add("CONTESTED");
        }

        return $"[{string.Join(Separator, parts)}]";
    }

    public static string Format(Context.Domain.Entities.Memory memory, bool includeFacts = false)
    {
        var note = $"{Header(memory)} {memory.Title}: {memory.Body}";
        if (!includeFacts)
        {
            return note;
        }

        var facts = EntityExtractor.ParseList(memory.FactsJson).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        return facts.Count == 0 ? note : $"{note} Facts: {string.Join("; ", facts)}";
    }
}
