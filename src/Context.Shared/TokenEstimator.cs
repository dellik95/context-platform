namespace Context.Shared;

/// <summary>
/// Cheap, provider-agnostic token estimation heuristic (~4 chars/token for mixed
/// code+prose content). Used for budget accounting everywhere in the platform —
/// this is NOT accurate enough for provider billing reconciliation.
/// </summary>
public static class TokenEstimator
{
    public static int Estimate(string text) => Math.Max(1, text.Length / 4);

    /// <summary>
    /// Same ~4-units/token heuristic as <see cref="Estimate(string)"/>, applied to a raw byte
    /// length instead of a decoded string's char count - for estimating what a file would cost to
    /// read in full without actually reading it (e.g. MCP search-tool savings accounting). Unlike
    /// <see cref="Estimate(string)"/>, this does not floor an unknown/empty length (&lt;= 0) at 1,
    /// since a size we don't actually know should contribute nothing rather than a fabricated token.
    /// </summary>
    public static int EstimateFromByteLength(long byteLength) => byteLength <= 0 ? 0 : (int)Math.Max(1, byteLength / 4);
}
