namespace Context.Retrieval.Scoring;

/// <summary>Combines normalized ranking signals into a single [0, 1] relevance score.</summary>
public interface IHybridScorer
{
    double Combine(NormalizedSignals signals);
}
