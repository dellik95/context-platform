using FluentAssertions;

namespace Context.Indexing.Tests;

public sealed class GitignoreStyleMatcherTests : IDisposable
{
    private readonly string _root;

    public GitignoreStyleMatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "context-indexing-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void WriteGitignore(string content) =>
        File.WriteAllText(Path.Combine(_root, ".gitignore"), content);

    private void WriteContextIgnore(string content) =>
        File.WriteAllText(Path.Combine(_root, ".contextignore"), content);

    // ---- Built-in structural denylist ----------------------------------------------------

    [Theory]
    [InlineData("node_modules/lodash/index.js")]
    [InlineData(".git/HEAD")]
    [InlineData("bin/Debug/App.dll")]
    [InlineData("obj/Debug/App.cs")]
    [InlineData(".venv/lib/python3.11/site.py")]
    [InlineData("dist/bundle.js")]
    [InlineData(".idea/workspace.xml")]
    [InlineData(".vscode/settings.json")]
    [InlineData(".claude/worktrees/agent-1/src/Program.cs")]
    [InlineData("apps/web/.claude/worktrees/agent-1/index.ts")]
    public void IsIgnored_BuiltInStructuralDenylist_MatchesNestedPaths(string relativePath)
    {
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored(relativePath).Should().BeTrue();
    }

    [Theory]
    [InlineData("package-lock.json")]
    [InlineData("yarn.lock")]
    [InlineData("pnpm-lock.yaml")]
    [InlineData("Cargo.lock")]
    [InlineData("packages.lock.json")]
    public void IsIgnored_BuiltInLockfiles_AreIgnoredByDefault(string relativePath)
    {
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored(relativePath).Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_OrdinarySourceFile_IsNotIgnoredByDefault()
    {
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("src/Program.cs").Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_EmptyPath_ReturnsFalse()
    {
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("").Should().BeFalse();
    }

    // ---- .gitignore / .contextignore file parsing ----------------------------------------

    [Fact]
    public void IsIgnored_PatternFromGitignoreFile_IsRespected()
    {
        WriteGitignore("*.log\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("app.log").Should().BeTrue();
        sut.IsIgnored("logs/app.log").Should().BeTrue();
        sut.IsIgnored("app.txt").Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_PatternFromContextIgnoreFile_IsRespected()
    {
        WriteContextIgnore("*.generated.cs\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("Foo.generated.cs").Should().BeTrue();
        sut.IsIgnored("Foo.cs").Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_CommentsAndBlankLinesInGitignore_AreSkipped()
    {
        WriteGitignore("# a comment\n\n   \n*.tmp\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("scratch.tmp").Should().BeTrue();
        // A line that is only a comment must not itself become a (broken) pattern.
        sut.IsIgnored("# a comment").Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_MissingGitignoreAndContextIgnore_DoesNotThrow()
    {
        var act = () => new GitignoreStyleMatcher(_root);

        act.Should().NotThrow();
    }

    [Fact]
    public void IsIgnored_ContextIgnoreNegation_OverridesGitignoreBecauseItIsAppliedLater()
    {
        // .contextignore is read after .gitignore, so it has higher precedence and can
        // un-ignore something the project's .gitignore excludes.
        WriteGitignore("*.log\n");
        WriteContextIgnore("!important.log\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("debug.log").Should().BeTrue();
        sut.IsIgnored("important.log").Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_BuiltInDenylistDirectoryName_MatchesTheBareDirectoryTooNotJustItsContents()
    {
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("bin").Should().BeTrue();
        sut.IsIgnored("node_modules").Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_QuestionMarkWildcard_MatchesExactlyOneNonSeparatorCharacter()
    {
        WriteGitignore("file?.txt\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("file1.txt").Should().BeTrue();
        sut.IsIgnored("file12.txt").Should().BeFalse();
        sut.IsIgnored("file.txt").Should().BeFalse();
    }

    // ---- Negation --------------------------------------------------------------------------

    [Fact]
    public void IsIgnored_NegationPattern_UnIgnoresASpecificFile()
    {
        WriteGitignore("*.log\n!important.log\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("debug.log").Should().BeTrue();
        sut.IsIgnored("important.log").Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_NegationOrderMatters_LaterPatternWins()
    {
        // Negate first, then re-ignore via a later, more specific pattern.
        WriteGitignore("*.log\n!important.log\nimportant.log\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("important.log").Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_AdditionalPatterns_TakeHighestPrecedenceAndCanUnIgnoreGitignoreEntries()
    {
        WriteGitignore("*.log\n");
        var sut = new GitignoreStyleMatcher(_root, additionalPatterns: ["!keep.log"]);

        sut.IsIgnored("other.log").Should().BeTrue();
        sut.IsIgnored("keep.log").Should().BeFalse();
    }

    // ---- Wildcards ---------------------------------------------------------------------------

    [Fact]
    public void IsIgnored_SingleStarWildcard_DoesNotCrossDirectorySeparators()
    {
        // Note: "output" is used rather than "build" because "build/" is itself part of the
        // built-in structural denylist and would mask what this test is trying to isolate.
        WriteGitignore("/output/*.txt\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("output/file.txt").Should().BeTrue();
        sut.IsIgnored("output/nested/file.txt").Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_DoubleStarWildcard_CrossesDirectorySeparators()
    {
        WriteGitignore("**/generated/**\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("a/b/generated/c/d.cs").Should().BeTrue();
        sut.IsIgnored("generated/c.cs").Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_AnchoredPattern_OnlyMatchesFromRoot()
    {
        WriteGitignore("/config.json\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("config.json").Should().BeTrue();
        sut.IsIgnored("nested/config.json").Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_UnanchoredPattern_MatchesAtAnyDepth()
    {
        WriteGitignore("temp\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("temp").Should().BeTrue();
        sut.IsIgnored("a/b/temp").Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_DirectoryPattern_IgnoresEverythingUnderneath()
    {
        WriteGitignore("cache/\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("cache/file.bin").Should().BeTrue();
        sut.IsIgnored("cache/nested/file.bin").Should().BeTrue();
    }

    // ---- Path normalization -------------------------------------------------------------------

    [Fact]
    public void IsIgnored_BackslashSeparatedPath_IsNormalizedLikeForwardSlash()
    {
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored(@"node_modules\lodash\index.js").Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_LeadingSlashInInputPath_IsHandledLikeARelativePath()
    {
        WriteGitignore("*.log\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("/app.log").Should().BeTrue();
    }

    // ---- Secret denylist: hard gate, cannot be silently disabled -------------------------------

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData(".env.production")]
    [InlineData("secrets.pem")]
    [InlineData("id_rsa.key")]
    [InlineData("aws-credentials.json")]
    [InlineData("my-credentials-backup.txt")]
    public void IsIgnored_SecretDenylist_MatchesByDefaultWithNoConfiguration(string relativePath)
    {
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored(relativePath).Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_SecretDenylist_CannotBeUnIgnoredByGitignoreNegation()
    {
        WriteGitignore("!.env\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored(".env").Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_SecretDenylist_CannotBeUnIgnoredByContextIgnoreNegation()
    {
        WriteContextIgnore("!*.pem\n");
        var sut = new GitignoreStyleMatcher(_root);

        sut.IsIgnored("server.pem").Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_SecretDenylist_CannotBeUnIgnoredByAdditionalPatterns()
    {
        // additionalPatterns is the highest-precedence *general* pattern list, yet the secret
        // gate is checked independently of, and before, the general pattern list entirely.
        var sut = new GitignoreStyleMatcher(_root, additionalPatterns: ["!.env", "!*.key"]);

        sut.IsIgnored(".env").Should().BeTrue();
        sut.IsIgnored("private.key").Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_SecretDenylist_RequiresExplicitDeliberateOverrideToDisable()
    {
        // The ONLY documented way to change secret-file behavior is the explicit constructor
        // argument — there is no boolean switch, and ordinary ignore-file syntax cannot reach it.
        var sut = new GitignoreStyleMatcher(_root, secretDenylistOverride: []);

        sut.IsIgnored(".env").Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_SecretDenylistOverride_ReplacesRatherThanExtendsTheDefaultList()
    {
        // Passing a custom override list means the *default* patterns (e.g. *.pem) no longer
        // apply — only the caller-provided ones do.
        var sut = new GitignoreStyleMatcher(_root, secretDenylistOverride: ["*.customsecret"]);

        sut.IsIgnored("server.pem").Should().BeFalse();
        sut.IsIgnored("app.customsecret").Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_SecretDenylistOverride_StillCannotBeReExposedByOrdinaryNegation()
    {
        WriteGitignore("!*.customsecret\n");
        var sut = new GitignoreStyleMatcher(_root, secretDenylistOverride: ["*.customsecret"]);

        sut.IsIgnored("app.customsecret").Should().BeTrue();
    }
}
