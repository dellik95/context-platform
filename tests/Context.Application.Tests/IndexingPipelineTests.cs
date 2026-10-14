using System.Runtime.CompilerServices;
using Context.Application.Abstractions;
using Context.Application.Indexing;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Indexing.Abstractions;
using Context.Infrastructure.Abstractions;
using Context.Application.Jobs;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Moq;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Application.Tests;

/// <summary>
/// BACKLOG-04: <see cref="IndexingPipeline.ReindexProjectAsync"/> must isolate a single file's
/// failure -- log it, count it, and keep going -- rather than letting one bad file abort the whole
/// pass (which would also skip the deleted-file cleanup and the final "Completed" progress signal
/// for every other file too). Uses a real temp directory (IndexFileInternalAsync touches the real
/// filesystem directly for File.Exists/ReadAllTextAsync) plus a fault-injecting fake
/// <see cref="ILanguageParser"/> that throws while parsing one specific file's content, with every
/// other collaborator a Moq mock -- following the same hand-rolled-mock construction pattern as
/// <c>ContextEngineTests</c>.
/// </summary>
public sealed class IndexingPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"context-pipeline-tests-{Guid.NewGuid():N}");

    private readonly Mock<IProjectRepository> _projectRepository = new();
    private readonly Mock<IFileRepository> _fileRepository = new();
    private readonly Mock<ISymbolRepository> _symbolRepository = new();
    private readonly Mock<IChunkRepository> _chunkRepository = new();
    private readonly Mock<IEmbeddingRepository> _embeddingRepository = new();
    private readonly Mock<IProviderResolver> _providerResolver = new();
    private readonly Mock<IFileScanner> _fileScanner = new();
    private readonly Mock<IContentHasher> _hasher = new();
    private readonly Mock<IJobQueue> _jobQueue = new();
    private readonly Mock<IIndexingProgressReporter> _progressReporter = new();
    private readonly List<ReindexProgressDto> _reportedProgress = [];
    private readonly object _reportedProgressLock = new();

    public IndexingPipelineTests()
    {
        Directory.CreateDirectory(_root);

        _fileRepository.Setup(f => f.UpsertAsync(It.IsAny<DomainFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DomainFile file, CancellationToken _) => file);
        _fileRepository.Setup(f => f.MarkDeletedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _symbolRepository.Setup(s => s.ListByFileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Symbol>)[]);
        _symbolRepository.Setup(s => s.ReplaceForFileAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Symbol>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _chunkRepository.Setup(c => c.ListByFileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Chunk>)[]);
        _chunkRepository.Setup(c => c.ReplaceForFileAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Chunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Guid>)[]);

        // No missing embeddings -> EnqueueEmbedIfMissingAsync short-circuits, keeping this test
        // focused on the per-file failure isolation rather than the embedding side-channel.
        _embeddingRepository.Setup(e => e.GetMissingContentHashesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);

        _providerResolver
            .Setup(r => r.GetEmbeddingGeneratorAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NoOpEmbeddingGenerator());

        _hasher.Setup(h => h.HashFile(It.IsAny<string>())).Returns((string path) => $"hash:{path}");
        _hasher.Setup(h => h.HashText(It.IsAny<string>())).Returns((string text) => $"hash:{text.GetHashCode()}");

        _jobQueue.Setup(q => q.EnqueueAsync(It.IsAny<JobType>(), It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        _progressReporter
            .Setup(r => r.ReportAsync(It.IsAny<ReindexProgressDto>(), It.IsAny<CancellationToken>()))
            .Callback<ReindexProgressDto, CancellationToken>((dto, _) =>
            {
                // Files now index concurrently (see IndexingPipeline.ReindexProjectAsync's
                // Parallel.ForEachAsync), so more than one file's progress callback can land here at
                // once -- List<T>.Add isn't safe for that without a lock.
                lock (_reportedProgressLock)
                {
                    _reportedProgress.Add(dto);
                }
            })
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// <see cref="IndexingPipeline.ReindexProjectAsync"/> now indexes each file inside its own
    /// freshly-resolved <see cref="IIndexingPipeline"/> (a real per-unit-of-work DI scope, matching
    /// production -- see that method's own remarks), so a bare <c>new IndexingPipeline(...)</c> with
    /// no backing container can no longer exercise it: the per-file scope's
    /// <c>GetRequiredService&lt;IIndexingPipeline&gt;()</c> would have nothing to resolve. This
    /// builds a real (tiny) <see cref="ServiceProvider"/> wired to the exact same mocks/fakes the
    /// tests already set up, registers <see cref="IIndexingPipeline"/> the same way
    /// ServiceCollectionExtensions.AddIndexingPipeline does (Scoped), and resolves the pipeline
    /// under test from it -- so every nested per-file scope resolves an equally-mocked pipeline.
    /// </summary>
    private IIndexingPipeline BuildPipeline(ILanguageParser parser)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_projectRepository.Object);
        services.AddSingleton(_fileRepository.Object);
        services.AddSingleton(_symbolRepository.Object);
        services.AddSingleton(_chunkRepository.Object);
        services.AddSingleton(_embeddingRepository.Object);
        services.AddSingleton(_providerResolver.Object);
        services.AddSingleton(_fileScanner.Object);
        services.AddSingleton<Func<string, IIgnoreMatcher>>(_ => Mock.Of<IIgnoreMatcher>());
        services.AddSingleton(parser);
        services.AddSingleton(_hasher.Object);
        services.AddSingleton(_jobQueue.Object);
        services.AddSingleton(_progressReporter.Object);
        services.AddScoped<IIndexingPipeline, IndexingPipeline>();

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IIndexingPipeline>();
    }

    [Fact]
    public async Task ReindexProjectAsync_isolates_a_single_file_failure_indexes_the_rest_and_reports_the_failure_count()
    {
        var projectId = Guid.NewGuid();
        var project = new Project { Id = projectId, Name = "Demo", RootPath = _root, CreatedAt = DateTimeOffset.UtcNow };

        // "bad.fake"'s content is the fault-injecting fake parser's trigger to throw mid-parse,
        // simulating malformed syntax / an unparsable file. "good.fake" has ordinary content and
        // must still be fully indexed despite bad.fake's failure elsewhere in the same pass.
        System.IO.File.WriteAllText(Path.Combine(_root, "good.fake"), "ordinary content");
        System.IO.File.WriteAllText(Path.Combine(_root, "bad.fake"), "THROW");

        // A previously-known file no longer present in this scan -- proves deleted-file cleanup
        // still runs even though an earlier file in the same pass failed to index.
        var removedFile = new DomainFile
        {
            Id = Guid.NewGuid(), ProjectId = projectId, RelativePath = "removed.fake",
            ContentHash = "old-hash", IsDeleted = false, UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };

        _projectRepository.Setup(p => p.GetByIdAsync(projectId, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        _fileRepository.Setup(f => f.GetByRelativePathAsync(projectId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DomainFile?)null); // every scanned file looks brand new
        _fileRepository.Setup(f => f.GetByRelativePathAsync(projectId, "removed.fake", It.IsAny<CancellationToken>()))
            .ReturnsAsync(removedFile); // ...except the one HandleFileRemovedAsync looks up during cleanup
        _fileRepository.Setup(f => f.ListByProjectAsync(projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DomainFile>)[removedFile]);

        var scanned = new List<ScannedFile>
        {
            new("good.fake", Path.Combine(_root, "good.fake"), 17),
            new("bad.fake", Path.Combine(_root, "bad.fake"), 5)
        };
        _fileScanner
            .Setup(s => s.ScanAsync(_root, It.IsAny<IIgnoreMatcher>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(scanned));

        var pipeline = BuildPipeline(new ThrowingFakeParser());

        // Must not throw: bad.fake's parser failure is isolated, not propagated out of the pass.
        await pipeline.ReindexProjectAsync(projectId, full: false, CancellationToken.None);

        // good.fake was actually indexed: exactly one ReplaceForFileAsync call, carrying the one
        // symbol the fake parser extracts. bad.fake never gets this far (it throws before this
        // point in IndexParsedFileAsync), so this must fire exactly once, not twice.
        _symbolRepository.Verify(
            s => s.ReplaceForFileAsync(It.IsAny<Guid>(), It.Is<IReadOnlyList<Symbol>>(l => l.Count == 1), It.IsAny<CancellationToken>()),
            Times.Once);

        // Deleted-file cleanup still ran despite bad.fake's earlier failure in the same pass.
        _fileRepository.Verify(f => f.MarkDeletedAsync(removedFile.Id, It.IsAny<CancellationToken>()), Times.Once);

        // The final "Completed" progress signal still went out, with both files counted as
        // processed and exactly one counted as failed.
        _reportedProgress.Should().NotBeEmpty();
        var final = _reportedProgress[^1];
        final.Completed.Should().BeTrue();
        final.FilesTotal.Should().Be(2);
        final.FilesProcessed.Should().Be(2);
        final.FilesFailed.Should().Be(1);

        // The failed file is retried by its own job instead of waiting for the next pass.
        _jobQueue.Verify(
            q => q.EnqueueAsync(
                JobType.IndexFile,
                It.Is<object>(p => p is IndexFilePayload && ((IndexFilePayload)p).RelativePath == "bad.fake" && ((IndexFilePayload)p).ProjectId == projectId),
                projectId, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _jobQueue.Verify(
            q => q.EnqueueAsync(JobType.IndexFile, It.Is<object>(p => ((IndexFilePayload)p).RelativePath == "good.fake"), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task IndexFileAsync_writes_the_content_hash_empty_first_and_records_the_real_one_as_its_last_step()
    {
        var projectId = Guid.NewGuid();
        _projectRepository.Setup(p => p.GetByIdAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = projectId, Name = "Demo", RootPath = _root, CreatedAt = DateTimeOffset.UtcNow });
        System.IO.File.WriteAllText(Path.Combine(_root, "ok.fake"), "ordinary content");
        var steps = new List<string>();
        _fileRepository.Setup(f => f.GetByRelativePathAsync(projectId, "ok.fake", It.IsAny<CancellationToken>())).ReturnsAsync((DomainFile?)null);
        _fileRepository.Setup(f => f.UpsertAsync(It.IsAny<DomainFile>(), It.IsAny<CancellationToken>()))
            .Callback<DomainFile, CancellationToken>((file, _) => steps.Add($"upsert:{file.ContentHash}"))
            .ReturnsAsync((DomainFile file, CancellationToken _) => file);
        _chunkRepository.Setup(c => c.ReplaceForFileAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Chunk>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, IReadOnlyList<Chunk>, CancellationToken>((_, chunks, _) => steps.Add($"chunks:{chunks.Count}"))
            .ReturnsAsync((IReadOnlyList<Guid>)[]);
        _fileRepository.Setup(f => f.SetContentHashAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((_, hash, _) => steps.Add($"hash:{hash}"))
            .Returns(Task.CompletedTask);

        await BuildPipeline(new ThrowingFakeParser()).IndexFileAsync(projectId, "ok.fake", CancellationToken.None);

        var realHash = $"hash:{Path.Combine(_root, "ok.fake")}";
        steps.First().Should().Be("upsert:");
        steps.Last().Should().Be($"hash:{realHash}");
        steps.Should().Contain(step => step.StartsWith("chunks:") && step != "chunks:0", "the new chunks were written before the hash was recorded");
    }

    [Fact]
    public async Task IndexFileAsync_never_records_the_content_hash_when_indexing_fails_part_way()
    {
        var projectId = Guid.NewGuid();
        _projectRepository.Setup(p => p.GetByIdAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = projectId, Name = "Demo", RootPath = _root, CreatedAt = DateTimeOffset.UtcNow });
        System.IO.File.WriteAllText(Path.Combine(_root, "bad.fake"), "THROW");
        _fileRepository.Setup(f => f.GetByRelativePathAsync(projectId, "bad.fake", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFile { Id = Guid.NewGuid(), ProjectId = projectId, RelativePath = "bad.fake", ContentHash = "old", UpdatedAt = DateTimeOffset.UtcNow });

        var act = () => BuildPipeline(new ThrowingFakeParser()).IndexFileAsync(projectId, "bad.fake", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _fileRepository.Verify(f => f.SetContentHashAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _fileRepository.Verify(f => f.UpsertAsync(It.Is<DomainFile>(file => file.ContentHash == string.Empty), It.IsAny<CancellationToken>()), Times.Once,
            "the interrupted attempt leaves the empty marker, so the retry starts from scratch");
    }

    [Theory]
    [InlineData("previous-hash", false)]
    [InlineData("", true)]
    public async Task IndexFileAsync_queues_the_graph_update_for_an_unparsed_file_only_on_a_first_or_unfinished_index(string existingHash, bool expectGraphJob)
    {
        var projectId = Guid.NewGuid();
        _projectRepository.Setup(p => p.GetByIdAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = projectId, Name = "Demo", RootPath = _root, CreatedAt = DateTimeOffset.UtcNow });
        System.IO.File.WriteAllText(Path.Combine(_root, "notes.txt"), "Some notes about retries.");
        _fileRepository.Setup(f => f.GetByRelativePathAsync(projectId, "notes.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFile { Id = Guid.NewGuid(), ProjectId = projectId, RelativePath = "notes.txt", ContentHash = existingHash, UpdatedAt = DateTimeOffset.UtcNow });

        // A file with no language parser is split by the tokenizer-backed chunker. This used to throw "tokenizer data
        // file Microsoft.ML.Tokenizers.Data.O200kBase.dll could not be loaded" because the data package was not
        // referenced, which failed every markdown/JSON/config file in production.
        await BuildPipeline(new ThrowingFakeParser()).IndexFileAsync(projectId, "notes.txt", CancellationToken.None);

        _chunkRepository.Verify(c => c.ReplaceForFileAsync(It.IsAny<Guid>(), It.Is<IReadOnlyList<Chunk>>(l => l.Count > 0 && l[0].Text.Contains("Some notes")), It.IsAny<CancellationToken>()), Times.Once);
        _jobQueue.Verify(
            q => q.EnqueueAsync(JobType.UpdateGraph, It.IsAny<object>(), projectId, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            expectGraphJob ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData("previous-hash", false)]
    [InlineData("", true)]
    public async Task IndexFileAsync_redoes_the_graph_and_embedding_work_for_an_unfinished_index_even_when_no_symbol_changed(string existingHash, bool redone)
    {
        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        _projectRepository.Setup(p => p.GetByIdAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = projectId, Name = "Demo", RootPath = _root, CreatedAt = DateTimeOffset.UtcNow });
        System.IO.File.WriteAllText(Path.Combine(_root, "same.fake"), "ordinary content");
        _fileRepository.Setup(f => f.GetByRelativePathAsync(projectId, "same.fake", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DomainFile { Id = fileId, ProjectId = projectId, RelativePath = "same.fake", ContentHash = existingHash, UpdatedAt = DateTimeOffset.UtcNow });

        // What an earlier, interrupted attempt already stored: the file's one symbol and its chunk, identical to
        // what the parser extracts now, so the symbol diff alone sees nothing to do.
        var symbolId = Guid.NewGuid();
        _symbolRepository.Setup(s => s.ListByFileAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Symbol { Id = symbolId, FileId = fileId, Name = "Foo", QualifiedName = "Foo", Kind = SymbolKind.Function, StartLine = 1, EndLine = 1, SignatureHash = "s", BodyHash = "b" }]);
        _chunkRepository.Setup(c => c.ListByFileAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Chunk { Id = Guid.NewGuid(), FileId = fileId, SymbolId = symbolId, Text = "Foo()\nreturn;", ContentHash = "c" }]);

        await BuildPipeline(new ThrowingFakeParser()).IndexFileAsync(projectId, "same.fake", CancellationToken.None);

        _jobQueue.Verify(
            q => q.EnqueueAsync(JobType.UpdateGraph, It.IsAny<object>(), projectId, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            redone ? Times.Once() : Times.Never());
        _embeddingRepository.Verify(
            e => e.GetMissingContentHashesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            redone ? Times.Once() : Times.Never());
    }

    /// <summary>
    /// Covers the cancellation wiring introduced for "stop an in-progress reindex" (see
    /// <see cref="Context.Application.Indexing.ReindexCancellationRegistry"/>): a token cancelled
    /// mid-scan must actually stop <see cref="IndexingPipeline.ReindexProjectAsync"/> rather than
    /// silently finish the pass, and must do so before the scan phase's buffering loop even hands
    /// control to the per-file indexing loop below it (see that method's own comment on why the
    /// scan is buffered into a list up front).
    /// </summary>
    [Fact]
    public async Task ReindexProjectAsync_throws_and_indexes_nothing_when_the_token_is_cancelled_mid_scan()
    {
        var projectId = Guid.NewGuid();
        var project = new Project { Id = projectId, Name = "Demo", RootPath = _root, CreatedAt = DateTimeOffset.UtcNow };

        System.IO.File.WriteAllText(Path.Combine(_root, "a.fake"), "one");
        System.IO.File.WriteAllText(Path.Combine(_root, "b.fake"), "two");

        _projectRepository.Setup(p => p.GetByIdAsync(projectId, It.IsAny<CancellationToken>())).ReturnsAsync(project);

        var cts = new CancellationTokenSource();
        var scanned = new List<ScannedFile>
        {
            new("a.fake", Path.Combine(_root, "a.fake"), 3),
            new("b.fake", Path.Combine(_root, "b.fake"), 3),
        };

        // Cancels right after yielding the first entry -- by the time the scan's own async
        // enumerator is asked for the second, the token this test passes to ReindexProjectAsync
        // is already cancelled, the same as a real POST /api/projects/{id}/index/cancel landing
        // while IFileScanner.ScanAsync is still walking the tree.
        _fileScanner
            .Setup(s => s.ScanAsync(_root, It.IsAny<IIgnoreMatcher>(), It.IsAny<CancellationToken>()))
            .Returns(CancelAfterFirstAsyncEnumerable(scanned, cts));

        var pipeline = BuildPipeline(new ThrowingFakeParser());

        var act = () => pipeline.ReindexProjectAsync(projectId, full: false, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        // The scan never finished buffering, so the per-file indexing loop never ran at all: no
        // file was indexed and no progress event (not even a partial one) was reported.
        _symbolRepository.Verify(
            s => s.ReplaceForFileAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Symbol>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _reportedProgress.Should().BeEmpty();
    }

    [Fact]
    public async Task IndexFileAsync_enqueues_missing_embeddings_as_jobs_of_the_provider_batch_size()
    {
        var projectId = Guid.NewGuid();
        _projectRepository.Setup(p => p.GetByIdAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = projectId, Name = "Demo", RootPath = _root, CreatedAt = DateTimeOffset.UtcNow });
        System.IO.File.WriteAllText(Path.Combine(_root, "multi.fake"), "multi");

        _embeddingRepository.Setup(e => e.GetMissingContentHashesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> hashes, string _, CancellationToken _) => (IReadOnlyList<string>)[.. hashes]);
        _providerResolver.Setup(r => r.GetEmbeddingGeneratorAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BatchingEmbeddingGenerator(new NoOpEmbeddingGenerator(), new EmbeddingBatchLimits(3, int.MaxValue)));
        var enqueued = new List<EmbedChunkPayload>();
        _jobQueue.Setup(q => q.EnqueueAsync(JobType.EmbedChunk, It.IsAny<object>(), projectId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<JobType, object, Guid?, int, CancellationToken>((_, payload, _, _, _) => enqueued.Add((EmbedChunkPayload)payload))
            .ReturnsAsync(Guid.NewGuid());

        await BuildPipeline(new MultiSymbolFakeParser()).IndexFileAsync(projectId, "multi.fake", CancellationToken.None);

        enqueued.Should().OnlyContain(p => p.ProjectId == projectId);
        enqueued.Select(p => p.Items.Count).Should().Equal(3, 1);
        var items = enqueued.SelectMany(p => p.Items).ToList();
        items.Select(i => i.ContentHash).Should().OnlyHaveUniqueItems();
        items.Select(i => i.ChunkIds.Count).Should().Equal(1, 1, 1, 2);
        items[^1].Text.Should().Contain("dup()");
    }

    private static async IAsyncEnumerable<ScannedFile> ToAsyncEnumerable(IEnumerable<ScannedFile> items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ScannedFile> CancelAfterFirstAsyncEnumerable(
        IReadOnlyList<ScannedFile> items, CancellationTokenSource cancelAfterFirst, [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return items[i];

            if (i == 0)
            {
                cancelAfterFirst.Cancel();
            }

            await Task.Yield();
        }
    }

    /// <summary>Throws mid-parse for any source text equal to the literal "THROW" -- the
    /// fault-injection trigger used by <c>bad.fake</c> above -- and otherwise extracts one
    /// trivial symbol so a successfully-parsed file still exercises the normal indexing path.</summary>
    private sealed class ThrowingFakeParser : ILanguageParser
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".fake" };

        public ParsedAst Parse(string sourceText, ParsedAst? previousTree = null) =>
            sourceText == "THROW"
                ? throw new InvalidOperationException("Simulated parser failure for BACKLOG-04's fault-injection test.")
                : new ParsedAst(new object(), sourceText);

        public IReadOnlyList<SymbolDraft> ExtractSymbols(ParsedAst ast) =>
            [new SymbolDraft("Foo", "Foo", SymbolKind.Function, 1, 1, "Foo()", "return;", DocComment: null, ParentQualifiedName: null)];

        public IReadOnlyList<RelationDraft> ExtractRelations(ParsedAst ast, IReadOnlySet<string>? projectSymbolNames = null) => [];
    }

    private sealed class MultiSymbolFakeParser : ILanguageParser
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".fake" };

        public ParsedAst Parse(string sourceText, ParsedAst? previousTree = null) => new(new object(), sourceText);

        // X.Dup and Y.Dup are byte-identical, so they share one content hash.
        public IReadOnlyList<SymbolDraft> ExtractSymbols(ParsedAst ast) =>
            [Draft("A", "a()"), Draft("B", "b()"), Draft("C", "c()"), Draft("X.Dup", "dup()"), Draft("Y.Dup", "dup()")];

        public IReadOnlyList<RelationDraft> ExtractRelations(ParsedAst ast, IReadOnlySet<string>? projectSymbolNames = null) => [];

        private static SymbolDraft Draft(string qualifiedName, string signature) =>
            new(qualifiedName.Split('.')[^1], qualifiedName, SymbolKind.Function, 1, 1, signature, "return;", DocComment: null, ParentQualifiedName: null);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
