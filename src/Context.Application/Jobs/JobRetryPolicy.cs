using Context.Domain.Enums;

namespace Context.Application.Jobs;

public readonly record struct JobRetryBudget(int MaxAttempts, TimeSpan MaxBackoff)
{
    /// <summary>Exponential backoff after <paramref name="attempts"/> failed attempts: 4s, 8s, 16s, ... up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BackoffAfter(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, attempts + 1)));
}

/// <summary>
/// Retry budget per job type, and which types carry data nobody else holds a copy of. A hook payload
/// or a memory the caller was told is "saving" exists only in its job row until the handler runs, so
/// those types get a longer budget and are protected from the bulk Pending cleanup.
/// </summary>
public static class JobRetryPolicy
{
    private static readonly TimeSpan DefaultBackoffCap = TimeSpan.FromMinutes(5);

    private static readonly JobType[] UserDataTypes =
    [
        JobType.ProcessHook,
        JobType.RememberMemory,
        JobType.ForgetMemory,
        JobType.ExtractMemory,
        JobType.SummarizeSession,
        JobType.ReconsolidateMemory,
    ];

    public static IReadOnlyList<JobType> UserData => UserDataTypes;

    public static bool IsUserData(JobType type) => Array.IndexOf(UserDataTypes, type) >= 0;

    public static JobRetryBudget For(JobType type) => type switch
    {
        JobType.ProcessHook or JobType.ForgetMemory => new(6, DefaultBackoffCap),
        // The embedding provider can stay down for a while; a saved memory should outlast that.
        JobType.RememberMemory => new(10, TimeSpan.FromMinutes(15)),
        JobType.EmbedChunk => new(5, DefaultBackoffCap),
        // Repairs wait on the same embedding provider whose outage usually caused them.
        JobType.RepairVectors => new(8, TimeSpan.FromMinutes(15)),
        _ => new(3, DefaultBackoffCap),
    };
}
