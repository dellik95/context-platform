namespace Context.Application.Memory.Triggers;

/// <summary>Fires once the just-produced chunk covers at least <c>Settings.MinEventsForMemoryExtraction</c>
/// events - the "N events" arm of the trigger check, reified out of <c>SummarizeSessionJobHandler</c>.</summary>
public sealed class EventThresholdExtractionTrigger : IMemoryExtractionTrigger
{
    public string Name => "EventThreshold";

    public bool ShouldFire(MemoryExtractionTriggerContext context) =>
        context.ChunkEventCount >= context.Settings.MinEventsForMemoryExtraction;
}
