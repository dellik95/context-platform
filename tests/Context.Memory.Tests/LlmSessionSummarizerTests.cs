using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Memory.Summarization;
using Context.Providers.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;

namespace Context.Memory.Tests;

public class LlmSessionSummarizerTests
{
    [Fact]
    public async Task SummarizeAsync_grounds_file_lists_in_events_not_LLM_prose()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var events = new List<SessionEvent>
        {
            Evt(SessionEventType.FileEdit, """{"path":"src/Real.cs"}"""),
        };

        var eventSource = new Mock<ISessionEventSource>();
        eventSource.Setup(s => s.GetEventsAsync(projectId, sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(events);

        var chatClient = new FakeChatClient(
            "TITLE:\nDid a thing\nCOMPLETED:\n- mentions src/Imagined.cs but never touched it\n");

        var sut = new LlmSessionSummarizer(eventSource.Object, new FakeProviderResolver(chatClient), new NoOpUsageRecorder());

        var summary = await sut.SummarizeAsync(projectId, sessionId, ct: CancellationToken.None);

        summary.ChangedFilesJson.Should().Contain("src/Real.cs");
        summary.ChangedFilesJson.Should().NotContain("Imagined");
        summary.FilesReadJson.Should().Be("[]");
    }

    [Fact]
    public async Task SummarizeAsync_falls_back_to_edited_file_title_when_LLM_omits_TITLE()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var events = new List<SessionEvent> { Evt(SessionEventType.FileEdit, """{"path":"src/Only.cs"}""") };

        var eventSource = new Mock<ISessionEventSource>();
        eventSource.Setup(s => s.GetEventsAsync(projectId, sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(events);

        var chatClient = new FakeChatClient("COMPLETED:\n- did something\n");

        var sut = new LlmSessionSummarizer(eventSource.Object, new FakeProviderResolver(chatClient), new NoOpUsageRecorder());

        var summary = await sut.SummarizeAsync(projectId, sessionId, ct: CancellationToken.None);

        summary.Title.Should().Be("Changes to src/Only.cs");
    }

    [Fact]
    public async Task SummarizeAsync_delegates_to_heuristic_builder_when_client_is_noop()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var events = new List<SessionEvent> { Evt(SessionEventType.FileRead, """{"path":"src/A.cs"}""") };

        var eventSource = new Mock<ISessionEventSource>();
        eventSource.Setup(s => s.GetEventsAsync(projectId, sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(events);

        var sut = new LlmSessionSummarizer(
            eventSource.Object, new FakeProviderResolver(new Context.Providers.Llm.NoOpChatClient()), new NoOpUsageRecorder());

        var summary = await sut.SummarizeAsync(projectId, sessionId, ct: CancellationToken.None);

        summary.FilesReadJson.Should().Contain("src/A.cs");
    }

    private static SessionEvent Evt(SessionEventType type, string payloadJson) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        Type = type,
        PayloadJson = payloadJson,
        Timestamp = DateTimeOffset.UtcNow
    };

    private sealed class FakeChatClient(string response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Always resolves to the one fixed IChatClient it was constructed with, regardless of
    /// projectId -- these tests don't exercise per-project provider selection.</summary>
    private sealed class FakeProviderResolver(IChatClient chatClient) : IProviderResolver
    {
        public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(Guid projectId, CancellationToken ct) =>
            throw new NotSupportedException("LlmSessionSummarizer never resolves an embedding generator.");

        public Task<IChatClient> GetChatClientAsync(Guid projectId, CancellationToken ct) => Task.FromResult(chatClient);
    }

    private sealed class NoOpUsageRecorder : IUsageRecorder
    {
        public Task RecordAsync(UsageRecord record, CancellationToken ct) => Task.CompletedTask;
    }
}
