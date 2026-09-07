using Context.Application.Settings;

namespace Context.Application.Memory.Triggers;

/// <summary>One named condition that can independently justify running memory extraction for a
/// just-produced SessionSummaryChunk. SummarizeSessionJobHandler fires extraction once ANY
/// registered trigger returns true - today that's exactly the two arms of the existing
/// `payload.IsSessionEnd || savedChunk.EventCount >= minEventsForExtraction` check, now named and
/// independently testable instead of inlined.</summary>
public interface IMemoryExtractionTrigger
{
    /// <summary>Logged verbatim when this trigger fires - keep stable, it's an observability key (spec §13).</summary>
    string Name { get; }

    bool ShouldFire(MemoryExtractionTriggerContext context);
}

public sealed record MemoryExtractionTriggerContext(
    bool IsSessionEnd,
    int ChunkEventCount,
    ProjectSettingsDto Settings);
