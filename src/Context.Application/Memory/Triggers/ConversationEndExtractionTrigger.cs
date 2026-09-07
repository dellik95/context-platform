namespace Context.Application.Memory.Triggers;

/// <summary>Fires unconditionally once the session is actually ending - the "session-end" arm of the
/// trigger check, reified out of <c>SummarizeSessionJobHandler</c>. Bypasses the event-count gate
/// entirely, since every remaining chunk must be processed then regardless of size.</summary>
public sealed class ConversationEndExtractionTrigger : IMemoryExtractionTrigger
{
    public string Name => "ConversationEnd";

    public bool ShouldFire(MemoryExtractionTriggerContext context) => context.IsSessionEnd;
}
