using Context.Application;
using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph;
using Context.Indexing;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Memory;
using Context.Providers;
using Context.Retrieval;
using Context.Retrieval.Abstractions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Context.IntegrationTests;

/// <summary>
/// Golden-query retrieval-quality evaluation
/// harness for the code-search path (<see cref="ISearchEngine.SearchAsync"/> -&gt;
/// <see cref="HybridSearchEngine"/> -&gt; <c>HybridScorer</c>). Before this test existed, the only
/// coverage of that path was formula-level unit tests against hand-rolled fakes
/// (<c>HybridScorerTests</c>, <c>HybridSearchEngineTests</c>) -- nothing exercised real FTS5 +
/// real sqlite-vec + real ranking end to end and reported an aggregate quality number.
///
/// Design (mirrors <see cref="GoldenPathE2ETests"/>'s zero-mock, offline-safe DI composition):
/// a small, self-contained ~12-file C# fixture corpus (12 thematic "modules", one class each, two
/// methods per class -- 24 symbols total) is written to a temp project root and fully reindexed.
/// A fixed set of golden queries then exercises <see cref="ISearchEngine.SearchAsync"/> directly.
///
/// Each query is a natural-language paraphrase of the target method's XML doc summary rather than
/// the identifier itself. That is deliberate: the default <c>AddContextProviders()</c> embedding
/// generator (<c>NoOpEmbeddingGenerator</c>) produces deterministic but semantically-meaningless
/// pseudo-vectors, so the vector-similarity signal carries no real relevance information in this
/// offline configuration -- only SQLite FTS5/BM25 lexical matching is a meaningful signal here.
/// Phrasing queries as prose (matching the doc-comment's separate word tokens) rather than as the
/// PascalCase identifier itself (a single FTS5 token) exercises that real lexical signal properly,
/// while correctness is verified independently by checking the PascalCase method/class name
/// appears in the returned hit's Label/Snippet -- not by string-matching the query itself.
///
/// Metrics: hit-rate@5 (fraction of queries where any expected identifier appears in the top-5
/// hits) and MRR (mean reciprocal rank of the first hit containing an expected identifier, 0 if
/// none of the returned hits do). Both are logged per-query and in aggregate via
/// <see cref="ITestOutputHelper"/> so a future change has a concrete, comparable number to check
/// against, not just a pass/fail.
/// </summary>
public sealed class RetrievalQualityHarnessTests : IAsyncLifetime
{
    private const int TopK = 5;

    private readonly DirectoryInfo _projectRoot = Directory.CreateTempSubdirectory("claudy-retrieval-eval-");
    private readonly ITestOutputHelper _output;
    private ServiceProvider? _provider;

    public RetrievalQualityHarnessTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        try
        {
            _projectRoot.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only; a lingering SQLite file handle on Windows must never fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task GoldenQueries_ReportHitRateAt5AndMrr_AgainstRealHybridSearch()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = cts.Token;

        foreach (var file in FixtureCorpus)
        {
            await System.IO.File.WriteAllTextAsync(Path.Combine(_projectRoot.FullName, file.RelativePath), file.Content, ct);
        }

        var dataDir = Path.Combine(_projectRoot.FullName, ".context");
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "context.db");

        // Real DI composition (AddContextProviders with no options => NoOpEmbeddingGenerator /
        // local-onnx reranker / no LLM -- zero API keys, zero network access), identical to
        // GoldenPathE2ETests.
        var services = new ServiceCollection();
        services.AddContextInfrastructure(dbPath);
        services.AddContextIndexing();
        services.AddContextGraph();
        services.AddContextProviders();
        services.AddContextRetrieval();
        services.AddContextMemory();
        services.AddContextApplication(_projectRoot.FullName, dataDir);

        _provider = services.BuildServiceProvider();

        await using (var migrationScope = _provider.CreateAsyncScope())
        {
            var db = migrationScope.ServiceProvider.GetRequiredService<ContextDbContext>();
            await db.Database.MigrateAsync(ct);
        }

        Project project;
        await using (var projectScope = _provider.CreateAsyncScope())
        {
            var projects = projectScope.ServiceProvider.GetRequiredService<IProjectRepository>();
            project = await projects.AddAsync(new Project
            {
                Id = Guid.NewGuid(),
                Name = "retrieval-eval-fixture",
                RootPath = _projectRoot.FullName,
                CreatedAt = DateTimeOffset.UtcNow
            }, ct);
        }

        var dispatcher = _provider.GetRequiredService<JobDispatcher>();
        await dispatcher.StartAsync(ct);
        try
        {
            await using (var indexScope = _provider.CreateAsyncScope())
            {
                var pipeline = indexScope.ServiceProvider.GetRequiredService<IIndexingPipeline>();
                await pipeline.ReindexProjectAsync(project.Id, full: true, ct);
            }

            await WaitForJobsToDrainAsync(_provider, TimeSpan.FromMinutes(5), ct);

            var perQueryResults = new List<QueryResult>();
            await using (var searchScope = _provider.CreateAsyncScope())
            {
                var engine = searchScope.ServiceProvider.GetRequiredService<ISearchEngine>();

                foreach (var query in GoldenQueries)
                {
                    var hits = await engine.SearchAsync(new SearchQuery(project.Id, query.QueryText, null, 2000), ct);

                    int? firstHitRank = null;
                    for (var i = 0; i < hits.Count; i++)
                    {
                        if (query.ExpectedIdentifiers.Any(id =>
                                hits[i].Label.Contains(id, StringComparison.Ordinal)
                                || (hits[i].Snippet?.Contains(id, StringComparison.Ordinal) ?? false)))
                        {
                            firstHitRank = i + 1; // 1-based rank
                            break;
                        }
                    }

                    perQueryResults.Add(new QueryResult(query, firstHitRank));
                }
            }

            // ---------------------------------------------------------------------------------------
            // Report: per-query outcome, then aggregate hit-rate@5 / MRR.
            // ---------------------------------------------------------------------------------------
            _output.WriteLine($"Golden-query retrieval harness -- {perQueryResults.Count} queries, top-{TopK} evaluated.");
            _output.WriteLine("");
            foreach (var result in perQueryResults)
            {
                var status = result.FirstHitRank switch
                {
                    { } rank when rank <= TopK => $"HIT  @rank {rank}",
                    { } rank => $"MISS (first correct hit at rank {rank}, outside top-{TopK})",
                    null => "MISS (no correct hit anywhere in results)"
                };
                _output.WriteLine($"[{result.Query.Module,-13}] \"{result.Query.QueryText}\" -> {status}");
            }

            var hitRateAt5 = perQueryResults.Count(r => r.FirstHitRank is { } rank && rank <= TopK) / (double)perQueryResults.Count;
            var mrr = perQueryResults.Average(r => r.FirstHitRank is { } rank ? 1.0 / rank : 0.0);

            _output.WriteLine("");
            _output.WriteLine($"hit-rate@{TopK} = {hitRateAt5:F3}  ({perQueryResults.Count(r => r.FirstHitRank is { } rank && rank <= TopK)}/{perQueryResults.Count})");
            _output.WriteLine($"MRR         = {mrr:F3}");

            // Regression floor, not a quality target: catches an outright breakage of code search
            // (e.g. lexical search returning nothing) without asserting a number no baseline run
            // has ever measured. See the harness's run log / PR description for the actual
            // before/after BACKLOG-13 measurements this floor was set relative to.
            hitRateAt5.Should().BeGreaterThanOrEqualTo(0.5,
                "a regression this large would mean code search is broken, not just imperfectly tuned");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    private sealed record QueryResult(GoldenQuery Query, int? FirstHitRank);

    private sealed record GoldenQuery(string Module, string QueryText, string[] ExpectedIdentifiers);

    private sealed record FixtureFile(string RelativePath, string Content);

    /// <summary>
    /// Mirrors GoldenPathE2ETests's WaitForJobsToDrainAsync: polls IJobRepository until no
    /// Pending/Running jobs remain (EmbedChunk / UpdateGraph for this test), bounded so a genuine
    /// regression (a job stuck retrying) fails the test instead of hanging.
    /// </summary>
    private static async Task WaitForJobsToDrainAsync(ServiceProvider provider, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = provider.CreateAsyncScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var pending = await jobs.ListByStatusAsync(JobStatus.Pending, null, ct);
            var running = await jobs.ListByStatusAsync(JobStatus.Running, null, ct);
            if (pending.Count == 0 && running.Count == 0)
            {
                return;
            }

            await Task.Delay(200, ct);
        }

        throw new TimeoutException($"Background jobs (embedding / graph) did not drain within {timeout}.");
    }

    // =============================================================================================
    // Fixture corpus: 12 thematic modules, one class each, two methods per class (24 symbols total).
    // =============================================================================================
    private static readonly FixtureFile[] FixtureCorpus =
    [
        new("OrderCalculator.cs", """
            namespace Fixture.Orders;

            /// <summary>Computes order totals and applies volume discounts.</summary>
            public class OrderCalculator
            {
                /// <summary>Computes the total price of an order, including quantity, unit price and tax.</summary>
                public decimal ComputeOrderTotal(int quantity, decimal unitPrice, decimal taxRate)
                {
                    var subtotal = quantity * unitPrice;
                    return subtotal + (subtotal * taxRate);
                }

                /// <summary>Applies a bulk discount when a customer orders a large quantity of items.</summary>
                public decimal ApplyBulkDiscount(decimal total, int quantity)
                {
                    return quantity >= 10 ? total * 0.9m : total;
                }
            }
            """),
        new("InventoryTracker.cs", """
            namespace Fixture.Inventory;

            /// <summary>Tracks stock levels across warehouse locations.</summary>
            public class InventoryTracker
            {
                /// <summary>Reserves stock for a given sku when a customer places an order, preventing overselling.</summary>
                public bool ReserveStock(string sku, int requestedQuantity)
                {
                    return requestedQuantity > 0;
                }

                /// <summary>Restocks a warehouse location after a supplier delivery arrives.</summary>
                public void RestockWarehouse(string sku, int incomingQuantity)
                {
                }
            }
            """),
        new("PaymentProcessor.cs", """
            namespace Fixture.Payments;

            /// <summary>Handles charging and refunding customer payments.</summary>
            public class PaymentProcessor
            {
                /// <summary>Charges a customer credit card for the given payment amount using the payment gateway.</summary>
                public string ChargeCreditCard(string cardToken, decimal amount)
                {
                    return "charged";
                }

                /// <summary>Refunds a previously captured payment back to the customer.</summary>
                public void RefundPayment(string transactionId, decimal amount)
                {
                }
            }
            """),
        new("AuthenticationService.cs", """
            namespace Fixture.Auth;

            /// <summary>Validates credentials and issues session tokens.</summary>
            public class AuthenticationService
            {
                /// <summary>Validates an incoming api key before granting access to protected resources.</summary>
                public bool ValidateApiKey(string apiKey)
                {
                    return !string.IsNullOrWhiteSpace(apiKey);
                }

                /// <summary>Issues a short-lived session token after a user successfully signs in.</summary>
                public string IssueSessionToken(Guid userId)
                {
                    return Guid.NewGuid().ToString();
                }
            }
            """),
        new("NotificationDispatcher.cs", """
            namespace Fixture.Notifications;

            /// <summary>Sends transactional emails and operational alerts.</summary>
            public class NotificationDispatcher
            {
                /// <summary>Sends an order confirmation email to the customer after checkout completes.</summary>
                public void SendOrderConfirmationEmail(string recipient, Guid orderId)
                {
                }

                /// <summary>Sends a low stock alert to the warehouse team when inventory falls below threshold.</summary>
                public void SendLowStockAlert(string sku)
                {
                }
            }
            """),
        new("SalesReportBuilder.cs", """
            namespace Fixture.Reporting;

            /// <summary>Builds and exports sales reporting.</summary>
            public class SalesReportBuilder
            {
                /// <summary>Builds a monthly revenue report summarizing all completed sales for the given period.</summary>
                public string BuildMonthlyRevenueReport(int year, int month)
                {
                    return $"report-{year}-{month}";
                }

                /// <summary>Exports a previously generated report to a downloadable csv file.</summary>
                public void ExportReportToCsv(string reportId)
                {
                }
            }
            """),
        new("ShippingCostEstimator.cs", """
            namespace Fixture.Shipping;

            /// <summary>Estimates shipping cost and selects a carrier.</summary>
            public class ShippingCostEstimator
            {
                /// <summary>Estimates the shipping cost for a package based on its weight and destination zip code.</summary>
                public decimal EstimateShippingCost(double weightKg, string destinationZip)
                {
                    return (decimal)weightKg * 1.5m;
                }

                /// <summary>Selects the best shipping carrier for a package based on weight and delivery speed.</summary>
                public string SelectCarrier(double weightKg, bool expedited)
                {
                    return expedited ? "express" : "standard";
                }
            }
            """),
        new("CustomerProfileManager.cs", """
            namespace Fixture.Customers;

            /// <summary>Manages customer profile data and account merges.</summary>
            public class CustomerProfileManager
            {
                /// <summary>Updates a customer saved shipping address after they move or make a correction.</summary>
                public void UpdateShippingAddress(Guid customerId, string newAddress)
                {
                }

                /// <summary>Merges two duplicate customer profiles into a single unified account record.</summary>
                public void MergeDuplicateProfiles(Guid primaryId, Guid duplicateId)
                {
                }
            }
            """),
        new("DiscountEngine.cs", """
            namespace Fixture.Discounts;

            /// <summary>Applies and expires promotional discount codes.</summary>
            public class DiscountEngine
            {
                /// <summary>Applies a promo code discount to an order total during checkout.</summary>
                public decimal ApplyPromoCode(string code, decimal orderTotal)
                {
                    return orderTotal;
                }

                /// <summary>Expires promotional codes that are older than the given cutoff date.</summary>
                public void ExpireOldPromotions(DateTime cutoffDate)
                {
                }
            }
            """),
        new("AuditLogger.cs", """
            namespace Fixture.Audit;

            /// <summary>Records and retains security-relevant audit events.</summary>
            public class AuditLogger
            {
                /// <summary>Records a security event performed by an actor for later compliance review.</summary>
                public void RecordSecurityEvent(string actor, string action)
                {
                }

                /// <summary>Purges audit log entries older than the configured retention period.</summary>
                public void PurgeExpiredLogs(int retentionDays)
                {
                }
            }
            """),
        new("ProductSearchIndexer.cs", """
            namespace Fixture.Search;

            /// <summary>Indexes and removes products from the customer-facing search index.</summary>
            public class ProductSearchIndexer
            {
                /// <summary>Indexes the full product catalog so customers can search for items by keyword.</summary>
                public void IndexProductCatalog(Guid catalogId)
                {
                }

                /// <summary>Removes a discontinued product from the search index so it no longer appears in results.</summary>
                public void RemoveDiscontinuedProduct(Guid productId)
                {
                }
            }
            """),
        new("ShiftScheduler.cs", """
            namespace Fixture.Scheduling;

            /// <summary>Assigns employee shifts and detects scheduling conflicts.</summary>
            public class ShiftScheduler
            {
                /// <summary>Assigns an employee to a work shift starting at the given date and time.</summary>
                public void AssignEmployeeShift(Guid employeeId, DateTime shiftStart)
                {
                }

                /// <summary>Detects overlapping shift conflicts for an employee schedule.</summary>
                public bool DetectShiftConflicts(Guid employeeId)
                {
                    return false;
                }
            }
            """),
    ];

    // =============================================================================================
    // Golden queries: natural-language paraphrases of a target method's doc summary. Each carries
    // 1-3 acceptable expected identifiers (the method name, and/or its containing class name as an
    // alternate acceptable match) rather than a GUID, so the set stays valid across re-indexing.
    // =============================================================================================
    private static readonly GoldenQuery[] GoldenQueries =
    [
        new("Orders", "compute the total price of an order including tax", ["ComputeOrderTotal", "OrderCalculator"]),
        new("Orders", "apply a bulk discount for a large quantity order", ["ApplyBulkDiscount", "OrderCalculator"]),
        new("Inventory", "reserve stock for a sku when a customer places an order", ["ReserveStock", "InventoryTracker"]),
        new("Inventory", "restock a warehouse location after a supplier delivery", ["RestockWarehouse", "InventoryTracker"]),
        new("Payments", "charge a customer credit card for a payment amount", ["ChargeCreditCard", "PaymentProcessor"]),
        new("Payments", "refund a previously captured payment to the customer", ["RefundPayment", "PaymentProcessor"]),
        new("Auth", "validate an api key before granting access", ["ValidateApiKey", "AuthenticationService"]),
        new("Auth", "issue a session token after a user signs in", ["IssueSessionToken", "AuthenticationService"]),
        new("Notifications", "send an order confirmation email after checkout", ["SendOrderConfirmationEmail", "NotificationDispatcher"]),
        new("Notifications", "send a low stock alert to the warehouse team", ["SendLowStockAlert", "NotificationDispatcher"]),
        new("Reporting", "build a monthly revenue report of completed sales", ["BuildMonthlyRevenueReport", "SalesReportBuilder"]),
        new("Reporting", "export a generated report to a csv file", ["ExportReportToCsv", "SalesReportBuilder"]),
        new("Shipping", "estimate shipping cost based on weight and destination zip code", ["EstimateShippingCost", "ShippingCostEstimator"]),
        new("Shipping", "select the best carrier based on delivery speed", ["SelectCarrier", "ShippingCostEstimator"]),
        new("Customers", "update a customer saved shipping address", ["UpdateShippingAddress", "CustomerProfileManager"]),
        new("Customers", "merge two duplicate customer profiles into one account", ["MergeDuplicateProfiles", "CustomerProfileManager"]),
        new("Discounts", "apply a promo code discount to an order total at checkout", ["ApplyPromoCode", "DiscountEngine"]),
        new("Discounts", "expire promotional codes older than a cutoff date", ["ExpireOldPromotions", "DiscountEngine"]),
        new("Audit", "record a security event performed by an actor for compliance", ["RecordSecurityEvent", "AuditLogger"]),
        new("Search", "index the product catalog so customers can search by keyword", ["IndexProductCatalog", "ProductSearchIndexer"]),
        new("Scheduling", "assign an employee to a work shift", ["AssignEmployeeShift", "ShiftScheduler"]),
    ];
}
