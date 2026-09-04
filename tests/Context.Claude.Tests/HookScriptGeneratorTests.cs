using Context.Claude.Installation;
using FluentAssertions;

namespace Context.Claude.Tests;

public class HookScriptGeneratorTests
{
    private static readonly HookDefinition Hook = HookDefinition.All[0];
    private static readonly ClaudeIntegrationOptions Options = new();

    [Fact]
    public void GenerateBash_IncludesContainerFallbackHosts()
    {
        var script = HookScriptGenerator.GenerateBash(Hook, Options);

        script.Should().Contain("host.docker.internal");
        script.Should().Contain("172.17.0.1");
        script.Should().Contain("/.dockerenv");
    }

    [Fact]
    public void GenerateBash_LogsErrorsInsteadOfSwallowingThem()
    {
        var script = HookScriptGenerator.GenerateBash(Hook, Options);

        script.Should().Contain(".claude/hooks.log");
        script.Should().Contain("log_error");
        script.Should().NotContain("2>/dev/null\nexit 0");
    }

    [Fact]
    public void GenerateBash_AlwaysFailsOpen()
    {
        var script = HookScriptGenerator.GenerateBash(Hook, Options);

        script.TrimEnd().Should().EndWith("exit 0");
        script.Should().NotContain("\nset -e\n");
    }

    [Fact]
    public void GeneratePowerShell_IncludesContainerFallbackHosts()
    {
        var script = HookScriptGenerator.GeneratePowerShell(Hook, Options);

        script.Should().Contain("host.docker.internal");
        script.Should().Contain("172.17.0.1");
        script.Should().Contain("/.dockerenv");
    }

    [Fact]
    public void GeneratePowerShell_LogsErrorsInsteadOfSwallowingThem()
    {
        var script = HookScriptGenerator.GeneratePowerShell(Hook, Options);

        script.Should().Contain(".claude/hooks.log");
        script.Should().Contain("Write-HookError");
    }

    [Fact]
    public void GeneratePowerShell_AlwaysFailsOpen()
    {
        var script = HookScriptGenerator.GeneratePowerShell(Hook, Options);

        script.TrimEnd().Should().EndWith("exit 0");
    }
}
