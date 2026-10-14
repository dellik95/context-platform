using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Application.Jobs;

public static class JobFactory
{
    public static Job Create(JobType type, object payload, Guid? projectId = null, int priority = 0) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        ProjectId = projectId,
        PayloadJson = JsonSerializer.Serialize(payload, payload.GetType()),
        Status = JobStatus.Pending,
        Priority = priority,
        Attempts = 0,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
