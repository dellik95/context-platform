using Context.Retrieval; // ContextPackBuilder, ContextPackOptions
using Context.Retrieval.Abstractions;
using Context.Retrieval.Tests.TestDoubles;
using Context.Shared;
using FluentAssertions;

namespace Context.Retrieval.Tests;

public class ContextPackBuilderTests
{
    private static SearchQuery Query(int tokenBudget) =>
        new(ProjectId: Guid.NewGuid(), Text: "does not matter", Scope: null, TokenBudget: tokenBudget);

    /// <summary>Builds a snippet whose <see cref="TokenEstimator.Estimate"/> is exactly <paramref name="tokens"/>.</summary>
    private static string SnippetOfTokens(int tokens) => new('a', Math.Max(1, tokens) * 4);

    // ---- Never exceeds budget -------------------------------------------------------------

    [Fact]
    public async Task BuildAsync_PrimarySelection_NeverExceedsItsBudget_AcrossVariedRandomCosts()
    {
        // Leave a small safety-reserve headroom (as the real default ratios do) so the
        // documented always-≥1-token floor of TokenEstimator.Estimate on the (possibly empty)
        // architecture-summary string can never push the *total* pack over budget either.
        var options = new ContextPackOptions
        {
            ArchitectureReserveRatio = 0,
            PrimaryReserveRatio = 0.9,
            HistoricalReserveRatio = 0,
            OpenIssuesReserveRatio = 0,
            SafetyReserveRatio = 0.1,
        };

        var random = new Random(42);
        var hits = Enumerable.Range(0, 40)
            .Select(i =>
            {
                var cost = random.Next(1, 97);
                var score = random.NextDouble() * 10;
                return new SearchHit(Guid.NewGuid(), "Symbol", $"hit-{i}", score, SnippetOfTokens(cost));
            })
            .ToList();

        const int budget = 500;
        var primaryBudget = (int)(budget * options.PrimaryReserveRatio);
        var builder = new ContextPackBuilder(new FakeSearchEngine(hits), options);

        var pack = await builder.BuildAsync(Query(budget), CancellationToken.None);

        var primaryTokensUsed = pack.PrimaryHits.Sum(h => TokenEstimator.Estimate(h.Snippet ?? h.Label));
        primaryTokensUsed.Should().BeLessThanOrEqualTo(primaryBudget);
        pack.EstimatedTokens.Should().BeLessThanOrEqualTo(budget);
    }

    [Fact]
    public async Task BuildAsync_EstimatedTokens_NeverExceedsTokenBudget_WithDefaultSectionRatios()
    {
        var random = new Random(7);
        var hits = Enumerable.Range(0, 25)
            .Select(i => new SearchHit(Guid.NewGuid(), "File", $"file-{i}", random.NextDouble(), SnippetOfTokens(random.Next(1, 60))))
            .ToList();
        var historicalNotes = Enumerable.Range(0, 10).Select(i => SnippetOfTokens(random.Next(1, 40))).ToList();
        var openIssues = Enumerable.Range(0, 10).Select(i => SnippetOfTokens(random.Next(1, 40))).ToList();

        const int budget = 1000;
        var builder = new ContextPackBuilder(new FakeSearchEngine(hits)); // default ContextPackOptions

        var pack = await builder.BuildAsync(Query(budget), historicalNotes, openIssues, CancellationToken.None);

        pack.EstimatedTokens.Should().BeLessThanOrEqualTo(budget);
    }

    [Fact]
    public async Task BuildAsync_ZeroTokenBudget_ProducesEmptyPackWithinBudget()
    {
        var hits = new[] { new SearchHit(Guid.NewGuid(), "Symbol", "only-hit", 1.0, "some snippet text") };
        var builder = new ContextPackBuilder(new FakeSearchEngine(hits));

        var pack = await builder.BuildAsync(Query(0), CancellationToken.None);

        // TokenEstimator.Estimate floors every string - including the empty architecture
        // summary - at 1 token, so a zero budget still reports 1 estimated token overall.
        pack.EstimatedTokens.Should().Be(1);
        pack.PrimaryHits.Should().BeEmpty();
        pack.Truncated.Should().BeTrue();
        pack.MoreAvailable.Should().Be(1);
    }

    [Fact]
    public async Task BuildAsync_NegativeTokenBudget_IsTreatedAsZero_AndNeverThrows()
    {
        var hits = new[] { new SearchHit(Guid.NewGuid(), "Symbol", "only-hit", 1.0, "some snippet text") };
        var builder = new ContextPackBuilder(new FakeSearchEngine(hits));

        var pack = await builder.BuildAsync(Query(-100), CancellationToken.None);

        pack.EstimatedTokens.Should().Be(1); // see the token-floor note above
        pack.PrimaryHits.Should().BeEmpty();
    }

    // ---- Truncated / MoreAvailable correctness ---------------------------------------------

    [Fact]
    public async Task BuildAsync_MoreHitsThanFit_SetsTruncatedTrue_AndCountsLeftoverExactly()
    {
        var options = new ContextPackOptions
        {
            ArchitectureReserveRatio = 0,
            PrimaryReserveRatio = 1.0,
            HistoricalReserveRatio = 0,
            OpenIssuesReserveRatio = 0,
            SafetyReserveRatio = 0,
        };

        // Five hits, each costing 20 tokens, strictly decreasing score => strictly decreasing
        // score/cost ratio => a fully deterministic greedy order regardless of ties.
        var hits = new[]
        {
            new SearchHit(Guid.NewGuid(), "Symbol", "h1", 5.0, SnippetOfTokens(20)),
            new SearchHit(Guid.NewGuid(), "Symbol", "h2", 4.0, SnippetOfTokens(20)),
            new SearchHit(Guid.NewGuid(), "Symbol", "h3", 3.0, SnippetOfTokens(20)),
            new SearchHit(Guid.NewGuid(), "Symbol", "h4", 2.0, SnippetOfTokens(20)),
            new SearchHit(Guid.NewGuid(), "Symbol", "h5", 1.0, SnippetOfTokens(20)),
        };

        var builder = new ContextPackBuilder(new FakeSearchEngine(hits), options);

        // primaryBudget = (int)(40 * 1.0) = 40 => exactly the top two (20 + 20) fit.
        var pack = await builder.BuildAsync(Query(40), CancellationToken.None);

        pack.PrimaryHits.Should().HaveCount(2);
        pack.PrimaryHits.Select(h => h.Label).Should().Equal("h1", "h2");
        pack.Truncated.Should().BeTrue();
        pack.MoreAvailable.Should().Be(3);
    }

    [Fact]
    public async Task BuildAsync_AllHitsFitWithinBudget_SetsTruncatedFalse_AndMoreAvailableZero()
    {
        var options = new ContextPackOptions
        {
            ArchitectureReserveRatio = 0,
            PrimaryReserveRatio = 1.0,
            HistoricalReserveRatio = 0,
            OpenIssuesReserveRatio = 0,
            SafetyReserveRatio = 0,
        };

        var hits = new[]
        {
            new SearchHit(Guid.NewGuid(), "Symbol", "h1", 5.0, SnippetOfTokens(10)),
            new SearchHit(Guid.NewGuid(), "Symbol", "h2", 4.0, SnippetOfTokens(10)),
            new SearchHit(Guid.NewGuid(), "Symbol", "h3", 3.0, SnippetOfTokens(10)),
        };

        var builder = new ContextPackBuilder(new FakeSearchEngine(hits), options);

        var pack = await builder.BuildAsync(Query(100), CancellationToken.None);

        pack.PrimaryHits.Should().HaveCount(3);
        pack.Truncated.Should().BeFalse();
        pack.MoreAvailable.Should().Be(0);
    }

    [Fact]
    public async Task BuildAsync_HistoricalNotesOverflowingTheirBudget_ContributeToTruncatedAndMoreAvailable()
    {
        var options = new ContextPackOptions
        {
            ArchitectureReserveRatio = 0,
            PrimaryReserveRatio = 0,
            HistoricalReserveRatio = 0.5, // half of a 40-token budget => 20 tokens for notes
            OpenIssuesReserveRatio = 0,
            SafetyReserveRatio = 0.5,
        };

        var historicalNotes = new List<string>
        {
            SnippetOfTokens(10),
            SnippetOfTokens(10),
            SnippetOfTokens(10), // 30 tokens total > 20-token historical budget
        };

        var builder = new ContextPackBuilder(new FakeSearchEngine([]), options);

        var pack = await builder.BuildAsync(Query(40), historicalNotes, openIssues: null, CancellationToken.None);

        pack.HistoricalNotes.Should().HaveCount(2);
        pack.Truncated.Should().BeTrue();
        pack.MoreAvailable.Should().Be(1);
        pack.EstimatedTokens.Should().BeLessThanOrEqualTo(40);
    }

    // ---- Signature stability / order-independence ------------------------------------------

    [Fact]
    public async Task BuildAsync_Signature_IsIndependentOfSearchEngineResultOrder_ForTheSameIncludedSet()
    {
        var hits = new[]
        {
            new SearchHit(Guid.NewGuid(), "Symbol", "h1", 5.0, "short a"),
            new SearchHit(Guid.NewGuid(), "File", "h2", 3.0, "short b"),
            new SearchHit(Guid.NewGuid(), "Chunk", "h3", 1.0, "short c"),
        };

        // Large budget so every hit is selected regardless of ordering.
        var builderInOrder = new ContextPackBuilder(new FakeSearchEngine(hits));
        var builderReversed = new ContextPackBuilder(new FakeSearchEngine([.. hits.Reverse()]));

        var packInOrder = await builderInOrder.BuildAsync(Query(10_000), CancellationToken.None);
        var packReversed = await builderReversed.BuildAsync(Query(10_000), CancellationToken.None);

        packInOrder.PrimaryHits.Should().HaveCount(3);
        packReversed.PrimaryHits.Should().HaveCount(3);
        packInOrder.Signature.Should().Be(packReversed.Signature);
    }

    [Fact]
    public async Task BuildAsync_Signature_IsUnaffectedByScoreOrSnippetChanges_WhenIncludedNodeIdsAreTheSame()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var hitsOriginal = new[]
        {
            new SearchHit(idA, "Symbol", "a", 5.0, "alpha snippet"),
            new SearchHit(idB, "Symbol", "b", 4.0, "beta snippet"),
        };
        var hitsRescoredAndRelabeled = new[]
        {
            new SearchHit(idA, "File", "a-renamed", 0.6, "totally different snippet text"),
            new SearchHit(idB, "File", "b-renamed", 0.4, "another different snippet"),
        };

        var builder1 = new ContextPackBuilder(new FakeSearchEngine(hitsOriginal));
        var builder2 = new ContextPackBuilder(new FakeSearchEngine(hitsRescoredAndRelabeled));

        var pack1 = await builder1.BuildAsync(Query(10_000), CancellationToken.None);
        var pack2 = await builder2.BuildAsync(Query(10_000), CancellationToken.None);

        pack1.Signature.Should().Be(pack2.Signature);
    }

    [Fact]
    public async Task BuildAsync_Signature_DiffersWhenTheIncludedSetOfNodesDiffers()
    {
        var hitsA = new[] { new SearchHit(Guid.NewGuid(), "Symbol", "a", 1.0, "x") };
        var hitsB = new[] { new SearchHit(Guid.NewGuid(), "Symbol", "b", 1.0, "y") };

        var builderA = new ContextPackBuilder(new FakeSearchEngine(hitsA));
        var builderB = new ContextPackBuilder(new FakeSearchEngine(hitsB));

        var packA = await builderA.BuildAsync(Query(10_000), CancellationToken.None);
        var packB = await builderB.BuildAsync(Query(10_000), CancellationToken.None);

        packA.Signature.Should().NotBe(packB.Signature);
    }

    [Fact]
    public async Task BuildAsync_Signature_IsDeterministic_ForRepeatedCallsWithTheSameInput()
    {
        var hits = new[]
        {
            new SearchHit(Guid.NewGuid(), "Symbol", "a", 5.0, "alpha"),
            new SearchHit(Guid.NewGuid(), "Symbol", "b", 3.0, "beta"),
        };
        var builder = new ContextPackBuilder(new FakeSearchEngine(hits));
        var query = Query(10_000);

        var pack1 = await builder.BuildAsync(query, CancellationToken.None);
        var pack2 = await builder.BuildAsync(query, CancellationToken.None);

        pack1.Signature.Should().Be(pack2.Signature);
    }

    [Fact]
    public async Task BuildAsync_Signature_EmptyIncludedSet_IsStableAndNonNull()
    {
        var builder = new ContextPackBuilder(new FakeSearchEngine([]));

        var pack = await builder.BuildAsync(Query(1000), CancellationToken.None);

        pack.PrimaryHits.Should().BeEmpty();
        pack.Signature.Should().NotBeNullOrEmpty();
    }
}
