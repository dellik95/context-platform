namespace Context.Domain.Entities;

public class Project
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string RootPath { get; set; }
    public string? VcsRemote { get; set; }
    public string? ConfigJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
