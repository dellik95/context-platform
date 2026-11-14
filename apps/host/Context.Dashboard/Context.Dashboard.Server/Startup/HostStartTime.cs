namespace Context.Dashboard.Server.Startup;

/// <summary>
/// Captured once at startup so <c>/api/system/status</c> can report host process uptime.
/// </summary>
public readonly record struct HostStartTime(DateTimeOffset Value);
