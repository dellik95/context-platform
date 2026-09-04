using Context.Claude.Installation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Context.Claude;

/// <summary>DI wiring for Context.Claude — the Claude Code hook DTOs and integration installer.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IClaudeIntegrationInstaller"/> and its <see cref="ClaudeIntegrationOptions"/>.
    /// Called from Context.Dashboard.Server/Program.cs (and Context.Cli, for `context claude install`).
    /// </summary>
    public static IServiceCollection AddContextClaude(
        this IServiceCollection services, Action<ClaudeIntegrationOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<ClaudeIntegrationOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddSingleton<IClaudeIntegrationInstaller, ClaudeIntegrationInstaller>();
        return services;
    }
}
