namespace Context.Domain.Entities;

public class Project
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string RootPath { get; set; }
    public string? VcsRemote { get; set; }
    public string? ConfigJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>SHA-256 hash (base64) of the project's bearer token; null until an integration is installed.</summary>
    public string? BearerTokenHash { get; set; }
    public DateTimeOffset? BearerTokenIssuedAt { get; set; }
}
