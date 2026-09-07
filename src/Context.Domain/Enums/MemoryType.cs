namespace Context.Domain.Enums;

// EF Core stores this as a plain int ordinal in SQLite (no HasConversion on Memory.Type in
// MemoryConfiguration.cs) - any new value MUST be appended last, never inserted before an
// existing one, or every previously-stored Memory.Type value silently becomes wrong.
public enum MemoryType { Decision, Discovery, BugFix, Feature, Refactor, Change, Exploration, Architecture, Issue, Instruction }
