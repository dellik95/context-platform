using System.Text.Json;
using Context.Domain.Entities;

namespace Context.Memory.Summarization;

/// <summary>
/// Pure, deterministic fold of a session's append-only <see cref="SessionSummaryChunk"/> history
/// into the single current-picture <see cref="SessionSummary"/> row. No LLM call: chunk content is
/// produced upstream (per-chunk summarization), this just merges what already exists.
/// </summary>
public static class SessionSummaryMerger
{
    public static SessionSummary Merge(Guid sessionId, IReadOnlyList<SessionSummaryChunk> chunksOrderedBySequence)
    {
        string? title = null;
        foreach (var chunk in chunksOrderedBySequence)
        {
            if (!string.IsNullOrWhiteSpace(chunk.Title))
            {
                title = chunk.Title;
            }
        }

        return new SessionSummary
        {
            SessionId = sessionId,
            Title = title,
            Completed = MergeBulletField(chunksOrderedBySequence, c => c.Completed),
            Discovered = MergeBulletField(chunksOrderedBySequence, c => c.Discovered),
            Decisions = MergeBulletField(chunksOrderedBySequence, c => c.Decisions),
            FilesReadJson = MergeJsonArrayField(chunksOrderedBySequence, c => c.FilesReadJson),
            ChangedFilesJson = MergeJsonArrayField(chunksOrderedBySequence, c => c.ChangedFilesJson),
            OpenIssues = MergeBulletField(chunksOrderedBySequence, c => c.OpenIssues),
            NextSteps = MergeBulletField(chunksOrderedBySequence, c => c.NextSteps),
            GeneratedAt = chunksOrderedBySequence.Count == 0 ? default : chunksOrderedBySequence[^1].GeneratedAt
        };
    }

    // Completed/Discovered/Decisions/OpenIssues/NextSteps are free-text, one point per line
    // (see HeuristicSessionSummaryBuilder/SessionSummaryParser). Concatenate oldest-to-newest,
    // dropping exact-duplicate lines (case-sensitive, trim-compared) so a point already recorded
    // in an earlier chunk doesn't repeat when a later chunk restates it.
    private static string? MergeBulletField(IReadOnlyList<SessionSummaryChunk> chunks, Func<SessionSummaryChunk, string?> selector)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();

        foreach (var chunk in chunks)
        {
            var text = selector(chunk);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length > 0 && seen.Add(line))
                {
                    lines.Add(line);
                }
            }
        }

        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    // FilesReadJson/ChangedFilesJson are JSON string arrays (see LlmSessionSummarizer/
    // HeuristicSessionSummaryBuilder: JsonSerializer.Serialize(List<string>)). Union by value
    // across chunks, preserving first-seen order, and always re-serialize to a JSON array
    // (never null) to match that existing convention.
    private static string MergeJsonArrayField(IReadOnlyList<SessionSummaryChunk> chunks, Func<SessionSummaryChunk, string?> selector)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<string>();

        foreach (var chunk in chunks)
        {
            var json = selector(chunk);
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            List<string>? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<List<string>>(json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (parsed is null)
            {
                continue;
            }

            foreach (var value in parsed)
            {
                if (seen.Add(value))
                {
                    values.Add(value);
                }
            }
        }

        return JsonSerializer.Serialize(values);
    }
}
