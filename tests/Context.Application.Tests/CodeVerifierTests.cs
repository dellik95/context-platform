using Context.Application.Adapters;
using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using FluentAssertions;
using Moq;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Application.Tests;

public sealed class CodeVerifierTests
{
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly List<string> _files = ["src/Payments/WebhookClient.cs", "src/Payments/RetryPolicy.cs", "README.md"];
    private readonly List<Symbol> _symbols = [NewSymbol("WebhookClient", "Acme.Payments.WebhookClient")];
    private readonly Mock<IFileRepository> _fileRepository = new();
    private readonly Mock<ISymbolRepository> _symbolRepository = new();
    private readonly Mock<IProjectRepository> _projectRepository = new();

    public CodeVerifierTests()
    {
        _projectRepository.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = _projectId, Name = "Payments", RootPath = "/repo/payments", CreatedAt = DateTimeOffset.UtcNow });
        _fileRepository.Setup(r => r.ListByProjectAsync(_projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _files.Select(p => new DomainFile { ProjectId = _projectId, RelativePath = p, ContentHash = "hash" }).ToList());
        _symbolRepository.Setup(r => r.SearchByNameAsync(_projectId, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string name, int limit, CancellationToken _) =>
                _symbols.Where(s => s.QualifiedName.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList());
    }

    [Theory]
    [InlineData("src/Payments/WebhookClient.cs")]
    [InlineData("./src/Payments/WebhookClient.cs")]
    [InlineData("src\\Payments\\WebhookClient.cs")]
    [InlineData("/repo/payments/src/Payments/WebhookClient.cs")]
    [InlineData("Payments/WebhookClient.cs")]
    [InlineData("webhookclient.cs")]
    [InlineData("`src/Payments/WebhookClient.cs`")]
    public async Task A_path_the_index_holds_is_verified(string path)
    {
        var check = await CreateSut().CheckAsync(_projectId, [path], [], CancellationToken.None);

        check.Result.Should().Be(CodeCheckResult.Verified);
        check.Note.Should().StartWith("found in code: ");
    }

    [Theory]
    [InlineData("WebhookClient")]
    [InlineData("Payments.WebhookClient")]
    public async Task A_symbol_the_index_holds_is_verified(string symbol)
    {
        var check = await CreateSut().CheckAsync(_projectId, [], [symbol], CancellationToken.None);

        check.Should().Be(new CodeCheck(CodeCheckResult.Verified, "found in code: " + symbol));
    }

    [Fact]
    public async Task A_missing_path_in_an_indexed_directory_is_refuted()
    {
        var check = await CreateSut().CheckAsync(_projectId, ["src/Payments/StripeClient.cs"], [], CancellationToken.None);

        check.Should().Be(new CodeCheck(CodeCheckResult.Refuted, "missing from indexed directories: src/Payments/StripeClient.cs"));
    }

    [Theory]
    [InlineData("src/Billing/Invoice.cs")]
    [InlineData("src/Payments/notes.txt")]
    [InlineData("/other/repo/src/Payments/StripeClient.cs")]
    [InlineData("StripeClient.cs")]
    [InlineData("ments/WebhookClient.cs")]
    public async Task Absence_outside_what_the_index_covers_stays_unknown(string path)
    {
        var check = await CreateSut().CheckAsync(_projectId, [path], ["StripeGateway"], CancellationToken.None);

        check.Should().Be(CodeCheck.Unknown);
    }

    [Fact]
    public async Task Mixed_results_are_unknown()
    {
        var check = await CreateSut().CheckAsync(
            _projectId, ["src/Payments/WebhookClient.cs", "src/Payments/StripeClient.cs"], [], CancellationToken.None);

        check.Should().Be(CodeCheck.Unknown);
    }

    [Fact]
    public async Task A_project_without_indexed_files_is_unknown_and_the_index_is_loaded_once()
    {
        _files.Clear();
        var sut = CreateSut();

        (await sut.CheckAsync(_projectId, ["src/Payments/WebhookClient.cs"], ["WebhookClient"], CancellationToken.None)).Should().Be(CodeCheck.Unknown);
        (await sut.CheckAsync(_projectId, ["src/Payments/WebhookClient.cs"], [], CancellationToken.None)).Should().Be(CodeCheck.Unknown);

        _fileRepository.Verify(r => r.ListByProjectAsync(_projectId, false, It.IsAny<CancellationToken>()), Times.Once);
        _symbolRepository.Verify(r => r.SearchByNameAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private CodeVerifier CreateSut() => new(_fileRepository.Object, _symbolRepository.Object, _projectRepository.Object);

    private static Symbol NewSymbol(string name, string qualifiedName) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        QualifiedName = qualifiedName,
        SignatureHash = "sig",
        BodyHash = "body",
    };
}
