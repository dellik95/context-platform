using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

/// <summary>A session paired with its latest summary title (null if never summarized), as returned by <see cref="ISessionRepository.ListByProjectAsync"/>.</summary>
public sealed record SessionListItem(Session Session, string? Title);
