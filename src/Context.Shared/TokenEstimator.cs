namespace Context.Shared;

/// <summary>
/// Cheap, provider-agnostic token estimation heuristic (~4 chars/token for mixed
/// code+prose content). Used for budget accounting everywhere in the platform —
/// this is NOT accurate enough for provider billing reconciliation.
/// </summary>
public static class TokenEstimator
{
    public static int Estimate(string text) => Math.Max(1, text.Length / 4);
}
