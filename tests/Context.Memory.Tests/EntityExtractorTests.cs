using System.Text.Json;
using Context.Domain.Enums;
using Context.Memory.Consolidation;
using Context.Memory.Extraction;
using FluentAssertions;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Tests;

public sealed class EntityExtractorTests
{
    [Fact]
    public void Extract_finds_tickets_config_keys_identifiers_and_file_basenames()
    {
        var candidate = Candidate(
            title: "PAY-1234: PaymentsWebhookClient retries",
            body: "Set `WEBHOOK_MAX_RETRIES` in appsettings.json. See src/Payments/WebhookClient.cs:42 for the UTF-8 decoding.",
            filesModified: ["src/Payments/Retry/RetryPolicy.cs"]);

        var keys = EntityExtractor.Extract(candidate).Select(e => e.Key).ToList();

        keys.Should().Contain(
        [
            "ticket:pay-1234",
            "config:webhook_max_retries",
            "symbol:paymentswebhookclient",
            "file:retrypolicy.cs",
            "file:webhookclient.cs",
            "file:appsettings.json",
        ]);
        keys.Should().NotContain("ticket:utf-8");
        keys.Should().OnlyContain(k => EntityExtractor.IsStrong(k));
    }

    [Fact]
    public void Extract_keeps_the_display_form_and_lowercases_the_key()
    {
        var entity = EntityExtractor.Extract(Candidate(title: "OrderService timeouts", body: "OrderService gives up after 30s."))
            .Single(e => e.Key == "symbol:orderservice");

        entity.Display.Should().Be("OrderService");
    }

    [Fact]
    public void Extract_classifies_declared_entities_explicit_kinds_first()
    {
        var candidate = Candidate(title: "Retry budget", body: "Retries are capped.") with
        {
            Entities = ["symbol:Orders.Retry", "file:src/a/Checkout.ts", "ORDERS_TABLE", "retry budget"],
        };

        var entities = EntityExtractor.Extract(candidate);

        entities.Take(4).Select(e => e.Key).Should().Equal(
            "symbol:orders.retry", "file:checkout.ts", "config:orders_table", "term:retry budget");
        entities[0].Display.Should().Be("Orders.Retry");
        EntityExtractor.IsStrong("term:retry budget").Should().BeFalse();
    }

    [Fact]
    public void Extract_ignores_brand_names_single_hump_words_and_path_segments()
    {
        var candidate = Candidate(
            title: "GraphQL gateway",
            body: "Uses JavaScript and Redis. Handler lives in src/MyFeature/Handler.cs.");

        var keys = EntityExtractor.Extract(candidate).Select(e => e.Key).ToList();

        keys.Should().NotContain(k => k.StartsWith("symbol:", StringComparison.Ordinal));
        keys.Should().Contain("file:handler.cs");
    }

    [Fact]
    public void Extract_reads_facts_and_file_lists_of_a_stored_memory()
    {
        var memory = new DomainMemory
        {
            Title = "Orders table",
            Body = "Orders are stored in one table.",
            FactsJson = JsonSerializer.Serialize(new[] { "The table name comes from ORDERS_TABLE." }),
            FilesModifiedJson = JsonSerializer.Serialize(new[] { "src/Orders/OrderStore.cs" }),
        };

        EntityExtractor.Extract(memory).Select(e => e.Key).Should().Contain(["config:orders_table", "file:orderstore.cs"]);
    }

    [Fact]
    public void Extract_caps_the_entity_count()
    {
        var body = string.Join(' ', Enumerable.Range(1, EntityExtractor.MaxEntities + 20).Select(i => $"AB-{i}"));

        EntityExtractor.Extract(Candidate(title: "Tickets", body: body)).Should().HaveCount(EntityExtractor.MaxEntities);
    }

    [Theory]
    [InlineData("ticket:pay-1", true)]
    [InlineData("symbol:orderservice", true)]
    [InlineData("config:orders_table", true)]
    [InlineData("file:a.cs", true)]
    [InlineData("term:retry", false)]
    [InlineData("plain", false)]
    public void IsStrong_holds_for_ticket_symbol_config_and_file_keys(string key, bool strong)
    {
        EntityExtractor.IsStrong(key).Should().Be(strong);
    }

    [Fact]
    public void ExtractPaths_returns_paths_with_known_extensions_without_line_suffixes()
    {
        EntityExtractor.ExtractPaths("See src/a.cs:12 and docs/readme.md, not v1.2 or example.invalid.")
            .Should().Equal("src/a.cs", "docs/readme.md");
        EntityExtractor.ExtractPaths(null).Should().BeEmpty();
    }

    private static MemoryCandidate Candidate(string title, string body, IReadOnlyList<string>? filesModified = null) =>
        new(MemoryType.Fact, title, body, EvidenceSource.EventBatch, [], [], [], filesModified ?? []);
}
